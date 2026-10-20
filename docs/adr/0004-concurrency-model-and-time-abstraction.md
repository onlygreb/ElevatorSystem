# 0004. Concurrency model and time abstraction

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

Many callers can request the elevator at once (100+ concurrent requests must be handled
efficiently, with assignment in under 100 ms). The elevator's state changes must be atomic, and
safety commands such as an emergency stop must take effect immediately, not after queued requests.
Timing-dependent behaviour (travel time, door time, stuck detection) must be testable without
real waiting.

## Decision

- **Intake:** `ElevatorController.RequestElevator` validates the request and writes it to a bounded
  `Channel<T>` (many writers, single reader). A full channel rejects the request with a clear
  "busy" result instead of blocking the caller.
- **Single scheduler:** `ProcessRequests` is the only consumer of the channel and the only code
  that adds to the pending queue; safety commands clear it under the elevator's lock. There are no
  data races on scheduling state.
- **Atomic state:** `Elevator` protects its state with `System.Threading.Lock`, and each transition
  is check-and-set inside the lock. Safety commands take the lock directly and skip the queue.
  The lock never contains `await`, I/O, logging or event publishing.
- **Immutable reads:** callers receive `ElevatorSnapshot` records created inside the lock, so a
  reader never sees a half-applied transition.
- **Time:** all code gets time from `TimeProvider` and waits with
  `Task.Delay(delay, timeProvider, token)`. The non-testable APIs (`DateTime.Now`,
  `DateTimeOffset.UtcNow`, `Thread.Sleep`, `Task.Delay(int)`, timers without a provider) are banned
  in `src/` by the Roslyn BannedApiAnalyzers (`src/BannedSymbols.txt`), so a violation is a build error.

## Alternatives considered

- **A single global lock around everything:** correct but coarse. Request intake would contend
  with movement, and an emergency stop would wait behind the queue.
- **Actor framework (Akka.NET, Orleans):** a strong model, but a heavy dependency for one actor.
  The channel plus single consumer gives the same guarantee with the base library.
- **`ConcurrentQueue` polled by a timer:** wastes CPU, can't apply backpressure, and still needs
  a lock for multi-step transitions.

## Consequences

- Correctness rests on two simple, testable rules: one writer for the queue, one lock for the car.
- Tests run in virtual time with `FakeTimeProvider`. A minute of simulated travel runs in
  milliseconds, with no flaky sleeps.
- Contributors must use `TimeProvider` everywhere. The analyzer makes that impossible to forget.
