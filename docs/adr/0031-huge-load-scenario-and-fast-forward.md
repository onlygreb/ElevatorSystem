# 0031. A huge load scenario that fast-forwards the building

- **Status:** Accepted
- **Date:** 2026-10-20
- **Amends:** [0027](0027-admin-demo-scenarios.md) §1

## Context

The three demo scenarios (ADR-0027) bring in at most eighteen people at a human pace. They show the system
working, not how it holds up: the requirements ask for 100+ concurrent requests, and the load test (k6) only
exercises the HTTP edge. An admin should be able to put the whole system (the control loop, the broker, the
database, the hub and every browser) under heavy load from the admin view, and watch it cope, without waiting
a quarter of an hour for the car to work through the crowd.

## Decision

1. **A fourth scenario, Huge load** (`DemoScenario.HugeLoad`): `Simulation:HugeLoadPassengers` people (120 by
   default) arrive within about a minute, ride up to a floor, stay a moment and ride back down to leave. Half
   of them are impatient: while they wait they press the call button again about every two seconds, through
   `PassengerService.PressCallButton`, which the controller merges into the pending call.
2. **Fast-forward.** While any huge load passenger is acting, the car and every simulation run
   `Simulation:HugeLoadSpeed` times faster (8 by default, at most `ElevatorController.MaxSpeed`, 10):
   - `ElevatorController.SetSpeed` divides the travel time and the door countdown by the speed, from the next
     trip or door cycle on, so the car behaves exactly as at normal speed, only sooner. The watchdog keeps its
     normal limit: a fast trip gets more slack, never less, so a host slowed down by the load is not mistaken
     for a stuck car, and a jammed car is still caught. The fail-safe rules are unchanged.
   - Only a drive that can change its pace (`IVariableSpeedDrive`, implemented by the simulated drive) is
     sped up; `SetSpeed` refuses anything else, so real machinery never runs faster than designed.
   - The scenario runner keeps its own clock, which runs that much faster than the real one, and looks
     around that much more often. Scenarios already running speed up too.
   - The speed returns to 1 once the last huge load passenger is done, or the scenarios stop, and the car has
     served the calls left behind (stopping a crowd does not withdraw its calls, and at normal speed they would
     keep the car busy for minutes). A new engine term always starts at 1.
3. **Visible everywhere.** The status carries `Speed` (additive in `ElevatorStatusDto`), so browsers shorten
   the car's glide and the door countdown, and show a "Fast-forward 8×" badge. A change of speed is announced
   as a `SpeedChanged` activity with the status as it is. Because the speed changes without a new elevator
   version, the browser applies a status of the same version when it was taken later.

With the defaults a huge load lasts about four minutes, and sends about 30,000 button presses (around 130 a
second) and some 240 rides through the whole pipeline (measured in simulated time).

## Alternatives considered

- **A scaled `TimeProvider` for the engine:** it would also scale timestamps, the audit trail, leases and the
  clock the browsers learn from the status; only the durations should change.
- **More passengers at normal speed:** a car of eight people takes over ten minutes to serve them, which is
  too long to watch.
- **Extra presses straight to the HTTP API:** they would hit the per-client rate limit, which protects the API
  and is not what this scenario tests; the load test already covers the HTTP edge.

## Consequences

- At 8× the doors stay open for about 0.6 s; a person using the passenger view during a huge load will find
  the car hard to catch. It is a stress test, started and stopped by an admin, and audited like any scenario.
- A jammed car is caught after the normal watchdog limit (4.5 s with the default settings), which is many
  fast-forwarded trips long; that is the price of never raising a false fault when the load slows the host down.
