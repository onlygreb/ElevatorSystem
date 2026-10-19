# 0010. Testing strategy

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

The riskiest parts of this system are concurrency, timing and failure handling. These are the
areas where example-based unit tests miss bugs and where tests with real sleeps become flaky.
Security and privacy rules also need proof, not just intent.

## Decision

Test in layers, each with a clear job:

| Layer | Tooling | Proves |
|---|---|---|
| Architecture | xUnit over project files | Dependency direction, package policy |
| Domain | xUnit v3 table-driven theories, **CsCheck** property-based tests | Every transition; invariants over random sequences; FIFO order |
| Application | **FakeTimeProvider**, parallel producers | Behaviour in virtual time; no lost or duplicated requests under contention |
| API | WebApplicationFactory, **Verify** snapshots | Authorization matrix, validation, rate limits, headers, OpenAPI contract |
| UI | **bUnit**, **Playwright** + axe | Component behaviour, main flows, accessibility |
| Integration and chaos | **Testcontainers** (RabbitMQ, PostgreSQL) | Broker and database outages, restarts, failover |
| Load | **k6** | 100+ concurrent requests, p95 assignment under 100 ms |
| Test quality | **Stryker.NET** mutation testing | The tests actually catch injected faults |

Shared rules:
- xUnit v3 on Microsoft.Testing.Platform with Shouldly assertions
- no wall-clock waiting in unit tests
- every bug fix starts with a failing test
- the authorization matrix and the privacy tests are mandatory, not "nice to have"

## Alternatives considered

- **Coverage percentage as the main gate:** easy to game with assertion-free tests. The mutation
  score measures whether tests detect faults, so coverage stays as a floor only.
- **Mocks everywhere:** couples tests to implementation details. Hand-written fakes for ports and
  real infrastructure in containers give more confidence.

## Consequences

- CI needs Docker for the integration layer (available on GitHub-hosted Linux runners).
- Concurrency and timing bugs are found by generated scenarios instead of by luck.
- The suite will be larger than usual. Keeping unit tests in virtual time keeps it fast.
