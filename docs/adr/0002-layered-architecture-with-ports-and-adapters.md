# 0002. Layered architecture with ports and adapters

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

The elevator logic must be correct, thread-safe and easy to verify in isolation, while the system
around it (HTTP, messaging, persistence, UI) changes far more often. Infrastructure must never be
able to compromise the core, and the browser UI must never bypass the rules enforced by the API.

## Decision

Split the solution into projects with a strict dependency direction (dependencies point inwards):

| Project | Depends on | Purpose |
|---|---|---|
| `Domain` | nothing | Elevator state machine, requests, scheduling strategies |
| `Contracts` | nothing | HTTP DTOs and message contracts shared across processes |
| `Application` | Domain, Contracts | `ElevatorController`, use cases, ports (interfaces) |
| `Infrastructure` | Application | Adapters: RabbitMQ, PostgreSQL, audit, retention |
| `Engine` | Application, Infrastructure, ServiceDefaults | Hosts the control loop |
| `Api` | Application, Infrastructure, Web, ServiceDefaults | HTTP entry point; serves the UI |
| `Web` | Contracts | Blazor WebAssembly UI |
| `ConsoleDemo` | Application | Core scenario with no infrastructure |
| `ServiceDefaults` | nothing | Telemetry, health checks, resilience |
| `AppHost` | Api, Engine | Local orchestration |

- These rules are executable: `tests/ElevatorSystem.Architecture.Tests` fails the build if a
  project gains a reference it isn't allowed, or if a new project appears without a rule.
- `Domain` and `Contracts` may not reference any package.
- The root namespace is `ElevatorSystem`, not `Elevator`, so the central `Elevator` type never
  collides with a namespace of the same name.

## Alternatives considered

- **A single project with folders:** simpler, but dependency rules can't be enforced and the core
  can't be shown in isolation.
- **Microservices per concern:** operational cost far beyond what one elevator needs. The single
  process split that does pay off (API and engine) is covered by ADR-0003.

## Consequences

- The core can be built, tested and demonstrated with no infrastructure at all (`ConsoleDemo`).
- Adapters can be replaced (in-memory bus vs RabbitMQ) without touching the domain.
- Some mapping code is needed between domain types and contracts. That's the price of keeping wire
  formats stable while the domain evolves.
