# 0012. Elevator control loop and core semantics

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

ADR-0004 (concurrency), ADR-0005 (FIFO) and ADR-0007 (fail-safe) set the direction for the elevator
core. Implementing them raised concrete questions those records leave open:

- How do new requests keep flowing while the car is travelling?
- What does `MoveUp()` mean in time?
- How is a stuck car detected?
- What happens to other calls when the door opens at a floor, and to pending calls when the car goes out of service?
- How can the loop be tested deterministically in simulated time?

## Decision

1. **The controller runs an event loop.** `ProcessRequests` sleeps until whichever comes first: a
   new request, the end of the current trip or door cycle, or shutdown. Requests are applied as
   they arrive, including mid-trip, so the time to assign a request does not depend on travel time.
   The test suite releases 2,000 requests at once and checks that the 99th-percentile assignment
   time stays under the 100 ms budget.
2. **Movement is committed one floor at a time.** `MoveUp()` and `MoveDown()` commit the car to the
   adjacent floor: the state becomes `MovingUp` or `MovingDown`, and `CurrentFloor` is the floor
   being travelled to. The drive then performs the trip. After each trip the strategy is asked
   again whether to stop (`OpenDoor`) or continue. The car only reverses after stopping.
3. **Physical movement goes through a port with a timeout.** It goes through `IElevatorDrive`, and
   every trip is awaited with a timeout of `TravelTimePerFloor × StuckTolerance` (default 3). A
   timeout or a drive failure takes the car out of service with reason `Fault`. There is no
   separate watchdog thread: the timeout is part of the operation the loop already awaits, so it
   is exact and cannot drift.
4. **Opening the door serves the whole floor.** Every pending call for that floor is served: hall
   calls in either direction and car calls. A call for a floor whose door is already open is
   served on arrival. FIFO still decides the order of stops (ADR-0005).
5. **Going out of service cancels pending calls.** All pending calls are cancelled and reported in
   the event, and new requests are refused until `ReturnToService`. Holding calls through an
   outage would later serve passengers who have long gone.
6. **Merged requests are counted, not stored.** A pending call keeps its first request's identifier
   and time plus a count, so memory is bounded by `Building.MaxPendingCalls` (28 for floors 1–10)
   whatever the load.
7. **Events are published once per loop iteration,** after the next operation has started. Any
   observer that reacts to an event (the UI, a test) finds the next timer already scheduled.
   This makes the simulated-time tests deterministic without real sleeps.
8. **Safety commands act directly.** `EmergencyStop` and `StartMaintenance` take effect through the
   elevator's lock and cancel the operation in flight; they never wait behind the request queue.

## Alternatives considered

- **Applying requests only between trips:** simpler, but assignment would wait up to a full trip
  (1.2 s by default), far over the 100 ms budget.
- **A watchdog thread polling for progress:** more moving parts and a polling interval to tune,
  where a timeout on the awaited operation is exact.
- **Stopping only for the call that made the car stop:** passengers going to the same floor would
  watch the door close and wait for another visit.
- **Storing every merged request:** memory would grow with the number of button presses.

## Consequences

- While travelling, a snapshot's floor is the destination of the current trip. The UI animates
  towards it over `TravelTimePerFloor`, and a stuck car is reported at the floor it was heading to.
- Drives must honour cancellation, since safety commands and shutdown cancel trips in flight.
- Event sinks are called on the loop's thread (or a safety command's caller), so they must be
  thread-safe and fast. A sink that throws is logged and isolated.
