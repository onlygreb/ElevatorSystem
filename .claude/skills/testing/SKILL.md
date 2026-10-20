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
| `ElevatorSystem.Domain.Tests` | Every state transition, invariants, FIFO order | Table-driven theories, CsCheck | 1 ✅ |
| `ElevatorSystem.Application.Tests` | Controller under concurrency and simulated time, watchdog | FakeTimeProvider, parallel producers | 1 ✅ |
| `ElevatorSystem.Api.Tests` | Endpoints, authorization matrix, validation, rate limits, headers, OpenAPI | WebApplicationFactory, Verify | 2, 4 |
| `ElevatorSystem.Web.Tests` | Component rendering and behaviour | bUnit | 2 |
| `ElevatorSystem.Integration.Tests` | Real broker and database, failure scenarios, failover | Testcontainers | 3 |
| `ElevatorSystem.E2E.Tests` | Main user flows in a browser, accessibility | Playwright, Deque axe | 5 ✅ |
| `tests/load` | 100+ concurrent requests, p95 acceptance under 100 ms | k6 (Quality workflow) | 5 ✅ |
| `stryker-config.json` (Domain, Application) | The tests catch injected faults | Stryker.NET (Quality workflow) | 5 ✅ |

New test projects go under `tests/` and inherit `tests/Directory.Build.props` (xUnit v3 on
Microsoft.Testing.Platform, Shouldly, code coverage). Don't add those packages again.

## Helpers that already exist

- `Domain.Tests/TestElevators`:
  - `At(floor)`, `InState(state)` (reached through public transitions)
  - `Hall`, `Car`, `Request`, `SnapshotAt`, `AllValidCalls`
- `Domain.Tests/Scheduling/SynchronousDriver`: runs a strategy over a fixed workload with no time involved.
- `Application.Tests/ControllerHarness`: a controller on `FakeTimeProvider` where every trip and door
  cycle takes exactly one `Step`.
  - `CallElevatorAsync` / `SelectFloorAsync` submit a request and wait until the loop has taken it in
  - `NextAsync<T>` reads the next event without moving time
  - `AdvanceUntilAsync<T>` and `RunUntilIdleAsync` step time forward
  - for a trip that never arrives, advance `StuckTimeout` explicitly
- `Application.Tests/EventProbe`: records events and waits for them, with a 10 s real-time guard that
  fails with the events seen so far.
- `E2E.Tests/BrowserFixture`: one API process (`ApiServer`: single-process mode, HTTPS with the development
  certificate, short car timings, demo scenarios on, a test admin) and one headless Chromium for the whole run.
  - `NewPageAsync(locale)` opens a fresh context (own cookies and storage), so each test starts outside
  - the test classes share the `OneElevator` collection and run one at a time: they share one car
  - the fixture warms up once with generous timeouts: it loads the app and registers the test admin's
    authenticator (`AdminTests.RegisterAsync`, `Totp`), so a cold runner never fails the first test
  - `PassengerJourneyTests.BoardAsync` walks into the car wherever it is; `AdminTests.SignInAsync` signs in
    with a fresh code (never a replayed one) and fails with the page text and the API's output
  - `AccessibilityTests` run axe with the WCAG 2.2 A and AA tags and expect no violation
  - every navigation goes through `OpenAsync`, which reloads a stalled app start and logs it; failures report the
    page, the browser's log and the API's output
  - CI runs them in a step of their own (`ELEVATOR_E2E=defer` skips them in the main run): containers starting in
    parallel change the network interfaces and Chromium aborts requests in flight (`net::ERR_NETWORK_CHANGED`)

## Prove a new test can fail

Before trusting a new test, break the behaviour it covers, watch it fail, then restore the code.
For example, remove the `DoorOpen` check from `Elevator.CheckMove` (the property test reports "the
car never moves with its door open"), or drop the watchdog's `WaitAsync` (the stuck-car test times out).

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
dotnet test --project tests/ElevatorSystem.E2E.Tests                          # browser flows; needs dotnet dev-certs https
dotnet tool restore; cd tests/ElevatorSystem.Domain.Tests; dotnet stryker          # mutation testing (also Application.Tests)
```

## Quality gates

- CI runs the whole suite in Release with coverage on every push and pull request; the browser tests run
  in a step of their own after the others.
- Mutation score for Domain and Application: break below 80% (Stryker `thresholds.break`). The Quality
  workflow runs it weekly and on demand: Domain takes about 10 minutes, Application about an hour, too
  long for every pull request.
  - Each test project's `stryker-config.json` says what is mutated. Application mutates its deterministic
    core (controller, passenger registry, audit chain and trail, pseudonymizer, mappers); the engine host,
    the command handler and the demo runner are reached only through real-time engine tests, which would
    make every mutant slow.
  - Argument guards, exception messages and logging calls are ignored: mutating them proves nothing.
  - The runner is Stryker's Microsoft Testing Platform runner (`"test-runner": "mtp"`).
  - Last scores: Domain 96%, Application 82%. A surviving mutant is a question: write the test that
    kills it, or explain why it is equivalent.
- The load test runs in the same workflow; see `tests/load/README.md`.
- Line coverage for Domain and Application: at least 90%.
