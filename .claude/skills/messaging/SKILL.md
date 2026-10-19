---
name: messaging
description: RabbitMQ design for ElevatorSystem - exchange and queue topology, message envelope and versioning, publisher confirms, acknowledgement and retry rules, dead-lettering, idempotent consumers, leader election, trace propagation, broker security and the in-memory bus. Use when touching IMessageBus, anything RabbitMQ-related in Infrastructure, the Engine's consumers, or cross-service events.
---

# Messaging

Decisions behind this skill: ADR-0003 (RabbitMQ), ADR-0007 (fail-safe), ADR-0008 (no MassTransit).

## Library

`RabbitMQ.Client` 7.x (fully async) wrapped behind `IMessageBus` in Infrastructure. `InMemoryMessageBus`
(built on `Channel<T>`) implements the same interface for the console demo and tests.

## Topology

The Infrastructure layer declares it at startup, idempotently, in a dedicated `elevator` virtual host.

| Exchange (type) | Queue | Arguments | Consumer |
|---|---|---|---|
| `elevator.commands` (direct) | `elevator.commands.engine` | quorum, `x-single-active-consumer`, `x-delivery-limit: 5`, DLX `elevator.dlx` | Engine |
| `elevator.events` (topic) | `elevator.events.api` | quorum | API → SignalR |
| `elevator.events` (topic) | `elevator.events.audit` | quorum | Audit writer |
| `elevator.dlx` (fanout) | `elevator.deadletter` | quorum | Alert (any message here is a bug) |

Quorum queues give replicated durability and the delivery limit used for poison messages. The
single-active-consumer setting guarantees that only one engine instance ever drives the car.

## Envelope

- Contract records live in `ElevatorSystem.Contracts`, named with a version suffix (`ElevatorRequestedV1`).
  The type header carries `elevator.request.v1`.
- Required headers:
  - `message-id`: GUID v7
  - `correlation-id`
  - `traceparent`: W3C trace context
  - `idempotency-key`, when the request had one
  - `content-type: application/json`
- Persistent delivery mode. Serialise with `System.Text.Json` source generation.
- Changes are additive only. A breaking change means a new `V2` type, and consumers accept both
  until the old one is retired.

## Delivery guarantees

- **Publish:** publisher confirms are on. The API returns `202` only after the broker confirms. If
  the confirm fails or times out, the API returns `503` with `Retry-After`.
- **Consume:** manual acknowledgement **after** the effect is persisted (state snapshot written),
  so delivery is at least once. Prefetch is 32.
- **Idempotency:** consumers deduplicate on `message-id` (persisted with a time-to-live), so a
  redelivery is harmless.
- **Failures:**
  - a transient error is nacked with requeue, bounded by the delivery limit, after which the message goes to the dead-letter queue
  - a poison message (can't be deserialised or validated) is rejected without requeue and goes to the dead-letter queue immediately

## Leadership and failover

- The engine holds a PostgreSQL advisory lock (`pg_try_advisory_lock`) on a dedicated connection.
  Only the lock holder runs the control loop and consumes commands.
- If the lock or the connection is lost, the loop stops accepting work at once. The car finishes
  its current floor and opens its doors (fail-safe), and a standby instance takes over.

## Observability and health

- Propagate trace context through the headers via `Propagators.DefaultTextMapPropagator`.
  `ActivitySource` is named `ElevatorSystem.Messaging`.
- Metrics:
  - published, confirmed, acked, nacked and dead-lettered messages
  - consumer lag
  - assignment latency (enqueued → assigned) histogram; target p95 under 100 ms
- Automatic connection recovery is on. While disconnected, readiness reports `Degraded` and the
  API rejects commands with `503` instead of accepting them silently.

## Security

- Never use the `guest` user. Each service gets its own user with least privilege:
  - API: write to `elevator.commands`, read `elevator.events.api`
  - Engine: read commands, write events
- Credentials come from Aspire parameters or user-secrets locally and environment secrets in
  deployment. TLS is required outside local development.
- Consumers validate every message as if it were untrusted input.

## Tests

- Testcontainers (`Testcontainers.RabbitMq`) with the real topology.
- Required failure scenarios:
  - broker restart mid-stream loses nothing
  - consumer killed before ack → the message is redelivered and applied once
  - poison message ends up in the dead-letter queue
  - a second engine instance stays idle until the first one dies
