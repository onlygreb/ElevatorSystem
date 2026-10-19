# 0003. RabbitMQ as the message broker

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

Passenger requests arrive through the API, but the elevator is driven by a separate engine
process. That separation lets each side fail and restart independently, and accepted requests
must survive an engine restart. The traffic is a small number of commands (at most a few hundred
in a burst) that each need acknowledgement, retry, dead-lettering and low latency. Exactly one
engine may drive the car at any time.

## Decision

- Use **RabbitMQ** with **quorum queues**:
  - commands flow through a direct exchange to a single-active-consumer queue owned by the engine
  - events flow through a topic exchange to one queue per subscriber (UI fan-out, audit)
  - poison messages go to a dead-letter exchange
- Talk to it through **RabbitMQ.Client 7.x** behind an `IMessageBus` port, with an in-memory
  implementation for tests and the console demo.

Full topology and delivery rules are in the `messaging` skill.

## Alternatives considered

- **Apache Kafka:** built for high-volume, replayable, partitioned event logs. Per-message
  acknowledgement, delayed retry and dead-lettering have to be built by hand, and running it is
  heavier. For a few commands per second it adds cost without benefit.
- **No broker (API calls the engine directly):** simpler, but a request accepted while the engine
  is restarting would be lost, and the API and engine could no longer fail independently.
- **MassTransit on top of RabbitMQ:** convenient, but version 9 is commercially licensed (ADR-0008),
  and the abstraction hides the broker features this design relies on.

## Consequences

- Accepted requests survive engine restarts. A hot-standby engine can take over safely because
  only one consumer is ever active.
- The API returns `202 Accepted` only after a publisher confirm, so "accepted" means persisted.
- The system now depends on a broker. Readiness reports `Degraded` when the broker is unreachable,
  and the API refuses new commands with `503` instead of accepting them silently.
