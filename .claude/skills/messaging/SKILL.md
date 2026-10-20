---
name: messaging
description: RabbitMQ design for ElevatorSystem - exchange and queue topology, message envelope and versioning, publisher confirms, acknowledgement and retry rules, dead-lettering, idempotent consumers, leader election, trace propagation, broker security and the in-memory bus. Use when touching the messaging ports, anything RabbitMQ-related in Infrastructure, the Engine's consumers, or cross-service events.
---

# Messaging

Decisions behind this skill: ADR-0003 (RabbitMQ), ADR-0007 (fail-safe), ADR-0008 (no MassTransit),
ADR-0018 (how the adapter delivers, recovers and fans out).

## Ports and implementations

The ports live in `Application/Ports/Messaging.cs`; the messages in `Contracts/ElevatorMessages.cs`.

| Port | RabbitMQ (`Infrastructure/Messaging`) | In memory (`Application/InMemory/Messaging`) |
|---|---|---|
| `IElevatorCommandPublisher` | `RabbitMqElevatorCommandPublisher` | `InMemoryMessageBus` |
| `IElevatorCommandConsumer` | `RabbitMqElevatorCommandConsumer` | `InMemoryMessageBus` |
| `IElevatorUpdatePublisher` | `RabbitMqElevatorUpdatePublisher` | `InMemoryMessageBus` |
| `IElevatorUpdateSubscriber` | `RabbitMqElevatorUpdateSubscriber` | `InMemoryMessageBus` |

All RabbitMQ classes share one `RabbitMqConnection` per service. `RabbitMQ.Client` 7.x (async API)
is used directly: no MassTransit (ADR-0008).

## Registering in a host

```csharp
// RabbitMQ: the four ports, the shared connection, a hosted service that connects at start, and
// the "rabbitmq" health check (tag "ready", Degraded while disconnected).
builder.Services.AddRabbitMqMessaging();
// or override what configuration says, e.g. with an Aspire connection string:
builder.Services.AddRabbitMqMessaging(o => o.Uri = new Uri(builder.Configuration.GetConnectionString("messaging")!));

// Traces: export the adapter's spans.
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource(RabbitMqTelemetry.ActivitySourceName));

// In memory (console demo, single-process hosting, tests): one instance serves all four ports.
builder.Services.AddSingleton<InMemoryMessageBus>();
builder.Services.AddSingleton<IElevatorCommandPublisher>(sp => sp.GetRequiredService<InMemoryMessageBus>());
// ...and the same for IElevatorCommandConsumer, IElevatorUpdatePublisher, IElevatorUpdateSubscriber.
```

Configuration section `Messaging` (validated when the host starts; the password comes from secrets,
never from `appsettings*.json`):

| Key | Default | Meaning |
|---|---|---|
| `Uri` | none | `amqp://` or `amqps://` URI with credentials; overrides the keys below |
| `Host`, `Port` | `localhost`, `5672` | Broker endpoint |
| `VirtualHost` | `/` | Use `elevator` in deployments |
| `UserName`, `Password` | none, required | Per-service credentials |
| `UseTls` | `false` | Required outside local development |
| `ClientName` | `ElevatorSystem` | Connection name shown in the broker; use the service name |
| `SubscriberName` | `subscriber` | Prefix of this service's update queues, e.g. `api` |
| `ConnectTimeout`, `PublishTimeout`, `RecoveryInterval` | `00:00:05` each | Connecting, waiting for a confirm, retrying |
| `PrefetchCount` | `32` | Unacknowledged messages a consumer holds |

## Topology

Declared idempotently on every new connection (`RabbitMqTopology`); a queue that exists with other
arguments fails the declaration loudly.

| Exchange (type) | Queue | Arguments | Consumer |
|---|---|---|---|
| `elevator.commands` (direct, key `command.v1`) | `elevator.commands.engine` | quorum, `x-single-active-consumer`, `x-delivery-limit: 5`, DLX `elevator.dlx`, `x-dead-letter-strategy: at-least-once`, `x-overflow: reject-publish` | Engine |
| `elevator.updates` (topic, keys `elevator-update.v1`, `occupancy-update.v1`) | `elevator.updates.{SubscriberName}.{id}`, one per subscription | classic, exclusive, auto-delete, `x-max-length: 1000`, `x-overflow: drop-head` | API → SignalR (later: audit) |
| `elevator.dlx` (fanout) | `elevator.deadletter` | quorum | Alert: any message here is a fault |

Quorum queues give replicated durability and the delivery limit. The single active consumer
guarantees that only one engine instance ever drives the car; a standby's consumer waits and takes
over when the active one's channel or connection goes away.

## Envelope

- Contract records live in `ElevatorSystem.Contracts` with a version suffix (`HallCallCommandV1`) and are
  serialised with `ContractsJsonContext`; commands carry a `type` discriminator (`hall-call.v1`, `car-call.v1`, `service.v1`).
- Properties: persistent; `message-id` = `CommandId` / `UpdateId`; `correlation-id` = trace ID (or the
  message ID); AMQP `type` = message type; `content-type: application/json`; timestamp from `TimeProvider`.
- Header `traceparent` (and `tracestate`), injected with `DistributedContextPropagator`.
- Changes are additive only. A breaking change means a new `V2` type, and consumers accept both
  until the old one is retired.

## Delivery guarantees

- **Publish:** publisher confirms with tracking on one shared channel. `PublishAsync` completes only
  after the broker's ack; a nack, a return (commands are `mandatory`), no confirm within
  `PublishTimeout`, or no connection throws `MessagingUnavailableException`. The API answers `202`
  only after a confirm and `503` with `Retry-After` otherwise.
- **Consume:** manual acknowledgement after the handler completes, one message at a time, prefetch
  `PrefetchCount`. Delivery is at least once: the engine drops commands whose `CommandId` it has
  already applied; subscribers drop repeated `UpdateId`s.
- **Failures:**
  - commands: a handler exception is a nack with requeue; after the delivery limit (the sixth
    failure) the broker dead-letters it (`x-first-death-reason: delivery_limit`)
  - commands: a message that is not a valid command (bad JSON, unknown type, undefined enum value,
    empty ID, wrong content type, over 64 KiB) is rejected without requeue: straight to the dead-letter queue
  - commands: a consumer that stops or dies leaves the message unacknowledged and it is redelivered
  - updates: a handler is tried three times, then the subscriber copies the update to `elevator.dlx`
    with `x-elevator-dead-letter-reason: handler-failed` (or `malformed`) and acknowledges it
  - updates: a slow subscriber's queue drops its oldest updates; the next status supersedes them

## In-memory bus

`InMemoryMessageBus` (constructor: logger, `maxDeliveryAttempts` 5, `commandCapacity` 1024,
`subscriberCapacity` 1024) keeps the same rules inside one process: immediate confirmation (a full
command queue throws `MessagingUnavailableException`), one active consumer with takeover,
redelivery of a command whose consumer stopped mid-handler, bounded retries then drop-and-log, and a
bounded drop-oldest inbox per subscriber. Nothing survives the process.

## Leadership and failover

- The engine holds a PostgreSQL advisory lock (`pg_try_advisory_lock`) on a dedicated connection.
  Only the lock holder runs the control loop and consumes commands.
- If the lock or the connection is lost, the engine stops driving and consuming at once, and a standby
  takes over: it restores the saved snapshot and opens the door where the car stands. Only a graceful
  stop parks the car with its door open before releasing the lock (ADR-0021).
- The queue's single active consumer is the second guard: even two consumers never both receive commands.

## Hosting (ADR-0021)

- **Engine** (`src/ElevatorSystem.Engine`, `Application/Hosting/ElevatorEngine`): migrate (a hosted service
  started before the persistence services), then repeat: acquire the lease, restore the snapshot, open the
  door where the car stands, drive while consuming. Lease lost: stop driving, consuming and saving at once.
  Graceful stop: consumption first, then the loop, park the car with its door open, flush, release. Role
  at `/health/role` and in the `elevator-engine` readiness check.
- **Commands:** `EngineCommandHandler` marks the `CommandId` in `IProcessedCommandStore` before applying it
  (a copy is dropped; a crash between the mark and the next save loses that call), submits calls with
  `RequestId = CommandId`, reports refusals as a `RequestRejected` update and acknowledges once the loop has
  taken the call in. An unreachable deduplication store applies the command anyway.
- **Updates:** one `ElevatorUpdateV1` per event and one `OccupancyUpdateV1` per change of the counts, from a
  bounded drop-oldest buffer; an elevator update waits until its snapshot is saved (unless saving fails).
  After `Engine:StatusRefreshInterval` (5 s) idle the latest updates are re-sent **with the same ids**, so a
  late subscriber catches up and the others drop the copies.
- **API:** publishes commands (`503` with `Retry-After: 2` and reason `Busy` on `MessagingUnavailableException`)
  and reads `ElevatorStateCache`, fed by the subscription in arrival order with copies dropped by id.
- **Modes:** `Hosting:Mode` = `Distributed` (default; RabbitMQ) or `SingleProcess` (the engine inside the API
  over `InMemoryMessageBus` and the in-memory stores, through the same `AddElevatorEngine`; Development and the
  API tests).
- **Aspire:** resources `messaging` (RabbitMQ 4.1 + management) and `postgres`/`elevatordb` (17.6); the
  connection string `messaging` becomes `Messaging:Uri`, `elevatordb` is the persistence default. User names are
  parameters, passwords generated parameters in the AppHost user secrets. Two engine replicas, one API.

## Connection, recovery and health

- One connection per service. `RabbitMqConnectionStarter` connects and declares the topology when the
  host starts, retrying every `RecoveryInterval`; it never blocks or fails the start (ADR-0007).
- After that, the client's automatic recovery restores the connection, channels, queues, bindings and
  consumers. A channel the broker closes while the connection stays up is replaced by the consumer loop.
- While disconnected: publishing fails at once with `MessagingUnavailableException`, consumers wait,
  and the `rabbitmq` health check (tag `ready`) reports `Degraded`.

## Observability

- `ActivitySource` `ElevatorSystem.Messaging` (`RabbitMqTelemetry.ActivitySourceName`, also used by the
  in-memory bus): a producer span `publish {exchange}` and a consumer span `process {queue}` whose parent
  is the publisher's context, so a request's trace continues into the engine.
- Logs (event IDs 4000–4099 RabbitMQ, 4100–4199 in memory) carry message IDs, types, queue names and an
  endpoint without credentials, never message bodies.
- Not built yet: metrics (published, confirmed, acked, nacked, dead-lettered, consumer lag, assignment
  latency histogram with a p95 target under 100 ms).

## Security

- Never use the `guest` user. Each service gets its own user with least privilege:
  - API: write to `elevator.commands`, read its `elevator.updates.api.*` queues
  - Engine: read commands, write updates
- Credentials come from Aspire parameters or user-secrets locally and environment secrets in
  deployment. TLS is required outside local development. `MessagingOptions` never prints the
  password, and validation errors name the key, never the value.
- Consumers validate every message as if it were untrusted input; the engine still validates floors.

## Tests

- `tests/ElevatorSystem.Messaging.Tests`: Testcontainers (`rabbitmq:4.1`), one container per collection
  (a second one for the restart test), the real topology. Proven there:
  - every command type and both update types arrive (updates at every subscriber), with the envelope headers
  - a consumer killed before acking (connection dropped broker-side) or stopped → redelivered and handled
  - a handler that always throws → dead-lettered after the delivery limit
  - a malformed message → dead-letter queue at once
  - two engines: only one receives commands; the other takes over when it stops
  - an unreachable broker → `MessagingUnavailableException`, `Degraded` health, consumers keep retrying
  - the trace context crosses the broker
  - a broker restart loses no confirmed command, and clients recover by themselves
  - logs hold IDs and types, never bodies
- `tests/ElevatorSystem.Application.Tests/InMemory/Messaging`: the same semantics for the in-memory bus.
- `tests/ElevatorSystem.Integration.Tests`: the whole system (Testcontainers broker and database, the API on the
  test server, engines as child processes): a hall call reaches hub clients, a duplicate is applied once, the
  standby takes over from a killed leader, a restarted engine restores floor and calls, a broker outage gives
  `503` and recovers, the API serves the last state while no engine runs.
