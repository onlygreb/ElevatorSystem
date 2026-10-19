# 0018. RabbitMQ messaging adapter: delivery, recovery and update fan-out

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

ADR-0003 chose RabbitMQ with quorum queues, publisher confirms and a dead-letter exchange, and the
`messaging` skill sketched the topology. Phase 3 implements the messaging ports
(`IElevatorCommandPublisher`, `IElevatorCommandConsumer`, `IElevatorUpdatePublisher`,
`IElevatorUpdateSubscriber`) on `RabbitMQ.Client` 7 and in memory. Building them raised questions
those records leave open:

- What does the broker do with a command whose consumer dies, and with one whose handler always fails?
- How do updates reach every API instance without piling up when an instance is slow or gone?
- What happens when the broker is down at start-up, or goes down later?
- What identifies a message, and how does the trace continue across the broker?
- What does the in-memory bus promise, since the console demo and tests rely on it?

## Decision

1. **Commands** go through the direct exchange `elevator.commands` to the quorum queue
   `elevator.commands.engine` with `x-single-active-consumer`, `x-delivery-limit: 5` and dead-letter
   exchange `elevator.dlx`, using the `at-least-once` dead-letter strategy (which needs
   `x-overflow: reject-publish`). Publishing is mandatory, so a command with no queue to go to is
   refused instead of dropped.
2. **Acknowledgement after the handler.** A handler exception is a `nack` with requeue, which the
   delivery limit bounds (the sixth failure dead-letters it). A message that is not a valid command
   (bad JSON, unknown type, undefined enum value, empty ID, wrong content type, over 64 KiB) is
   rejected without requeue and goes straight to `elevator.deadletter`. A consumer that stops leaves
   its message unacknowledged, so the broker hands it to the next consumer.
3. **Updates** go through the topic exchange `elevator.updates`, routed by type
   (`elevator-update.v1`, `occupancy-update.v1`). Each subscription declares its own **exclusive,
   auto-delete classic queue** (`elevator.updates.{subscriber}.{id}`) bounded to 1,000 messages with
   `drop-head`. It has no dead-letter exchange, because overflow would flood it. Instead, the
   subscriber retries a failing handler three times and then copies the update to `elevator.dlx`
   itself, with an `x-elevator-dead-letter-reason` header. Malformed updates go the same way.
   Updates are not mandatory: an update nobody listens to is not an error. The exchange is named
   after the `…UpdateV1` contracts; earlier documents call it `elevator.events`.
4. **One connection per service, automatic recovery.** `RabbitMqConnection` connects on first use or
   when the host starts (`RabbitMqConnectionStarter` retries in the background and never blocks the
   start). It declares the topology on every new connection and relies on the client's automatic
   connection and topology recovery after that. While disconnected, publishing fails at once with
   `MessagingUnavailableException`, consumers wait and retry, and the `rabbitmq` health check
   (tag `ready`) reports `Degraded`.
5. **Publishing waits for the confirm.** One channel with publisher confirmation tracking serves all
   publishers of a service. `PublishAsync` completes only after the broker's `ack`. A `nack`, a
   return, a timeout (`PublishTimeout`, default 5 s) or a lost connection throws
   `MessagingUnavailableException`.
6. **Envelope.** `message-id` is the command or update ID, so redeliveries are recognisable.
   `correlation-id` is the current trace ID, or the message ID without one. The AMQP `type` is the
   message type (`hall-call.v1`…), `content-type` is `application/json`, messages are persistent, and
   the W3C `traceparent` is injected through `DistributedContextPropagator`. Spans come from the
   `ElevatorSystem.Messaging` activity source in both implementations.
7. **The in-memory bus** (`InMemoryMessageBus`) keeps the same semantics where they make sense:
   - confirmation is immediate, and a full command queue throws `MessagingUnavailableException`
   - one consumer is active at a time, and a command left unacknowledged by a stopping consumer
     goes to the next one
   - a failing handler is retried five times, then the message is dropped and logged
   - each subscriber has a bounded, drop-oldest inbox

   Nothing survives the process.
8. **Logs** name messages by ID and type only, and the broker by an endpoint without credentials.
   Message bodies are never logged (ADR-0009).
9. The health check needs `Microsoft.Extensions.Diagnostics.HealthChecks` in Infrastructure. It is a
   Microsoft package under the MIT licence, and its version is aligned with the other
   `Microsoft.Extensions` packages.

## Alternatives considered

- **A durable quorum queue per subscriber:** it would survive restarts, but updates are superseded by
  the next status, and a queue left behind by a gone instance would grow until someone deleted it.
  Exclusive queues disappear with their connection.
- **Dead-lettering subscriber queues through `x-dead-letter-exchange`:** with `drop-head`, every
  dropped update would land in the dead-letter queue and drown real faults.
- **Retrying failed commands in-process:** this hides the retry count from the broker. Requeue plus
  the quorum delivery limit makes the count durable across consumer restarts.
- **Failing the host start when the broker is down:** this contradicts ADR-0007. A degraded service
  that reports itself is preferable to one that restarts in a loop.
- **RabbitMQ.Client's built-in OpenTelemetry instrumentation alone:** it is used only when its
  sources are listened to. Explicit spans and header injection keep the trace intact with or without it.

## Consequences

- Delivery is at least once. The engine must drop commands whose `CommandId` it has already applied,
  and subscribers use `UpdateId`.
- Anything in `elevator.deadletter` is a fault to alert on: a poison command, a command that failed
  six times, or an update a subscriber could not handle.
- A subscriber misses updates published while it is disconnected or before its queue is bound, so
  the hub sends the current status on (re)connect (ADR-0016).
- Topology arguments are fixed. Changing them (for example the delivery limit) needs a migration or
  a broker policy, because redeclaring a queue with different arguments fails.
- The broker's own users, virtual host and TLS are deployment configuration. The adapter enforces
  credentials and an `amqp`/`amqps` URI, nothing more.
