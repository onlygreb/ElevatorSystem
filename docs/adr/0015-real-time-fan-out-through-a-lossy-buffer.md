# 0015. Real-time fan-out through a lossy buffer

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

Browsers follow the elevator live through the SignalR hub at `/hubs/elevator` (ADR-0006). The
elevator's events come from the control loop, which must never wait for I/O (ADR-0007 §4, ADR-0012).
Sending to browsers is I/O at its slowest and least trustworthy: any client can be slow, stop reading
its socket, or disconnect halfway through a message, and SignalR awaits each send. ADR-0007 says events
leave "through a bounded buffer" but not what happens when it is full, and the threat model left that
open (OQ-5, T-ENG-05).

Two kinds of message reach the browser (`IElevatorClient` in the contracts):

- `ActivityOccurred`: one per domain event, a narration of what happened;
- `StatusChanged`: the whole current state, with a version number.

A status message carries the complete state, so a client that misses some activity is fully corrected
by the next status. Activity is the part a client can afford to lose.

## Decision

1. **The control loop's sink is a bounded, lossy buffer.** `ElevatorEventBuffer` implements
   `IElevatorEventSink` with a `Channel<T>` of `RealTime:EventBufferCapacity` events (default 1,024),
   many writers, one reader, `FullMode = DropOldest`. `Publish` never blocks and never throws.
2. **Overflow is visible, not silent.** Each dropped event is counted (`DroppedCount`) and logged as
   event 3000, at most once every 10 seconds, with the drops since the last report and the total, so a
   flood of drops cannot flood the log as well.
3. **One broadcaster drains the buffer on its own thread.** `ElevatorBroadcastService` sends each
   event to all clients as `ActivityOccurred`, in order. After each batch (at most 64 events) it sends
   one `StatusChanged` with the current snapshot, and only if the snapshot's version differs from the
   last status sent. A burst of hundreds of requests therefore produces a handful of status messages,
   never hundreds of identical ones.
4. **A failed send is logged (event 3001) and skipped.** Nothing a client does can stop the broadcast,
   and nothing the broadcaster does can reach the control loop.
5. **A new client gets the current status on connect,** from the hub itself, so it never waits for the
   next change to draw the car.
6. **The hub is receive-only.** It declares no client-callable methods; commands go through the REST
   API, where validation and rate limits live.

## Alternatives considered

- **Publishing to SignalR from the control loop:** the simplest code, and exactly the coupling ADR-0007
  forbids. One stalled socket would stall the car.
- **An unbounded buffer:** never drops, but a client that stops reading grows the process's memory
  without limit, which turns a slow browser into a denial of service.
- **Blocking (`FullMode = Wait`) or dropping the newest event:** blocking stalls the car again; dropping
  the newest keeps stale history and loses the most recent changes, which are the ones a lagging client
  needs.
- **Sending only status, no activity:** loses the step-by-step narration the passenger view and the
  logs-for-humans requirement rely on.
- **A status message per event:** correct but noisy; under load most of them would be identical.

## Consequences

- The car's timing is independent of every browser. Tests prove it: a hub whose sends never complete,
  and one whose sends always throw, leave the car serving requests on time.
- Under overload a client may miss activity lines. It still converges on the right state from the next
  status, and the drops are counted and logged. The UI must treat activity as best-effort narration and
  status (with its version) as the truth.
- SignalR awaits each send, so one slow client slows the broadcast for everyone until SignalR drops the
  connection; the buffer absorbs that, and drops activity if it lasts. Per-client connection limits for
  the hub are still open (OQ-11).
- In phase 3 the engine publishes events to RabbitMQ instead, and the API's broadcaster consumes them
  from its queue; the lossy buffer and the coalescing rules stay the same on the API side.
