---
name: testing
description: Testing strategy for ElevatorSystem - which test project and test type to use for a change, xUnit v3 and Shouldly conventions, deterministic time with FakeTimeProvider, property-based tests with CsCheck, concurrency tests, Testcontainers integration and chaos tests, bUnit and Playwright, mutation testing with Stryker.NET, coverage and the commands to run each suite. Use whenever writing or changing tests, or deciding how to prove a change works.
---

# Testing

Decision behind this skill: ADR-0010. The goal is tests that **catch bugs**, not tests that raise
coverage. The mutation score is the quality signal; coverage is only a floor.

## Where a test goes

| Project | What it proves | Tools | Phase |
|---|---|---|---|
| `ElevatorSystem.Architecture.Tests` | Dependency direction, package policy | xUnit, XML over project files | 0 ✅ |
| `ElevatorSystem.Domain.Tests` | Every state transition, invariants, FIFO order | Table-driven theories, CsCheck | 1 |
| `ElevatorSystem.Application.Tests` | Controller under concurrency and simulated time, watchdog | FakeTimeProvider, parallel producers | 1 |
| `ElevatorSystem.Api.Tests` | Endpoints, authorization matrix, validation, rate limits, headers, OpenAPI | WebApplicationFactory, Verify | 2, 4 |
| `ElevatorSystem.Web.Tests` | Component rendering and behaviour | bUnit | 2 |
| `ElevatorSystem.Integration.Tests` | Real broker and database, failure scenarios, failover | Testcontainers | 3 |
| `ElevatorSystem.E2E.Tests` | Main user flows in a browser, accessibility | Playwright, Deque axe | 2+ |
| `tests/load` | 100+ concurrent requests, p95 assignment under 100 ms | k6 | 5 |

New test projects go under `tests/` and inherit `tests/Directory.Build.props` (xUnit v3 on
Microsoft.Testing.Platform, Shouldly, code coverage). Don't add those packages again.

## Conventions

- **Names:** `Method_Scenario_Expectation`, for example `MoveUp_AtTopFloor_IsRejected`.
- **One behaviour per test,** with Arrange / Act / Assert separated by blank lines.
- **Assertions:** Shouldly, with a reason string when the failure wouldn't otherwise be obvious.
- **Test doubles:** hand-written fakes first; NSubstitute when a fake gets heavy. Moq and
  FluentAssertions are denied (ADR-0008).
- **No real waiting:**
  - never `Thread.Sleep` or `Task.Delay` with wall-clock time in unit tests
  - inject `FakeTimeProvider` and call `Advance(...)`
  - integration tests poll with a timeout helper, never fixed sleeps
- **Independence:** tests are parallel-safe and order-independent. Shared containers use an
  xUnit collection fixture.
- **Every bug fix starts with a failing test** that reproduces it.

## Techniques for the elevator core

- **Transition table:** one `[Theory]` row per (state, command) pair, giving the expected new
  state or the rejection.
- **Property-based (CsCheck):**
  - generate random sequences of requests and ticks
  - assert the invariants from the `elevator-domain` skill
  - assert that FIFO order is kept and that every accepted request is eventually served
  - when a property fails, copy the printed seed into a regression test
- **Concurrency:**
  - N producers released together with a `Barrier` call `RequestElevator` through `Parallel.ForEachAsync`
  - assert that no request is lost or duplicated, that duplicates are merged, and that latency stays within the budget
  - repeat the scenario to shake out interleavings
- **Simulation:** script a scenario ("call from 7 going down, then car call to 2"), advance fake
  time, and assert the exact sequence of domain events.

## Security, privacy and fail-safe tests are not optional

- **Authorization matrix:** every endpoint × {anonymous, Auditor, Operator, Admin} → expected status.
- **Validation and abuse:** malformed JSON, out-of-range floors, overposting, oversized payloads,
  rate limiting (429 with `Retry-After`), replayed idempotency keys.
- **Privacy:** captured logs contain no personal data; the retention job anonymises expired data;
  export returns all of the caller's data and nobody else's.
- **Fail-safe:**
  - the watchdog trips on a stuck car
  - a broker or database outage degrades readiness but doesn't stall the car
  - an engine restart restores state
  - a standby engine takes over

## Commands

```bash
dotnet test --solution ElevatorSystem.slnx                                    # everything
dotnet test --project tests/ElevatorSystem.Domain.Tests                       # one project
dotnet test --solution ElevatorSystem.slnx --coverage --coverage-output-format cobertura --results-directory TestResults
dotnet stryker --project ElevatorSystem.Domain.csproj                         # mutation testing (phase 1+, local tool)
```

## Quality gates

- CI runs the whole suite in Release with coverage on every push and pull request.
- Mutation score for Domain and Application: break below 80% (Stryker `thresholds.break`).
- Line coverage for Domain and Application: at least 90%.
