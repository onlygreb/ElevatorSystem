# 0032. Stop and clear empties the queue and sends the car to the lobby

- **Status:** Accepted
- **Date:** 2026-10-20
- **Amends:** [0027](0027-admin-demo-scenarios.md) §3, [0031](0031-huge-load-scenario-and-fast-forward.md) §2

## Context

"Stop and clear" removed the simulated passengers but left their calls in the queue. After a huge load that
meant up to 28 calls nobody was waiting for, and minutes of the car visiting empty floors. ADR-0031 ran the car
fast-forwarded until it had served them. An admin who presses "Stop and clear" expects a clean building: an
empty queue and the car back in the lobby.

## Decision

1. **The domain can clear the queue.** `Elevator.ClearRequests(keep)` cancels every pending call except those in
   `keep`, which keep their place in line, and reports them in a `RequestsCleared` event. It is allowed in every
   state and leaves the car itself as it is: a car already travelling arrives at the floor it was heading to.
2. **In order with the requests.** `ElevatorController.ClearRequests(keep, thenGoTo)` joins the same intake as the
   requests, so a request submitted before the clear is cleared too, and anything submitted after it stays. The
   control loop then sends the car to `thenGoTo` with a car call, unless the car is out of service.
3. **Stop and clear** removes every simulated passenger, keeps the calls of people still in the building (a hall
   call for each one waiting, a car call for each one riding to a floor they chose), clears everything else and
   sends the car to the lowest floor. The speed returns to normal at once.
4. **Visible**: the activity feed says `Queue cleared: N calls cancelled`, and the cancelled calls count in the
   `elevator.calls.cancelled` metric, as when the car goes out of service.

## Alternatives considered

- **Cancel every call, people's too:** a person waiting on a floor would see their call vanish while the UI still
  says they are waiting.
- **Serve the leftovers fast-forwarded (ADR-0031 before this):** the car still visits floors nobody is waiting on,
  just sooner.
- **Take the car out of service and back:** cancels people's calls too, and records a maintenance stop that never
  happened.

## Consequences

- The invariant "every accepted request is pending, served or cancelled, exactly once" now counts cancellations
  from `RequestsCleared` too; the property-based tests include a clearing step.
- A clear that finds the controller's intake full (1,024 requests waiting) is refused and logged (event 1848);
  pressing the button again clears.
