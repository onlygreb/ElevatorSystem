# 0025. The car waits with its door open, and a call starts the door countdown

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

Passengers now walk in and out of the car themselves (ADR-0026): they press "enter" while the door is
open at their floor and "leave" while it is open at any floor. Under ADR-0012 the door closed a fixed
time after every stop and the car then waited closed, so a passenger who arrived at an idle car had to
call it first even though it stood right there, and the UI could not tell how long they had to walk in.

## Decision

1. **With nowhere to go, the car waits with its door open.** At start-up, after a return to service, and
   whenever a stop leaves nothing pending, the door opens (or stays open) and no countdown runs.
2. **A call that gives the car somewhere to go starts the countdown**: the door closes
   `DoorOpenDuration` later (4 s by default) and the car leaves. At a stop with more calls pending, the
   door closes after the same duration as before.
3. **A call for the floor where the door is open is served on the spot**, as before, and the car keeps
   waiting there.
4. **The status says when the door will close.** `ElevatorStatusDto.DoorsCloseAt` is the time while a
   countdown runs and `null` otherwise (door open with nowhere to go, or door closed). The UI draws the
   countdown from it.
5. The policy is `ElevatorControllerOptions.HoldDoorOpenWhenIdle`, on by default. `false` restores the
   classic cycle (the door closes after every stop and the car waits closed), which the behaviour-focused
   controller suites keep using; `ElevatorControllerDoorTests` covers the default.

## Alternatives considered

- **Re-open the door on a hall call at the car's floor:** keeps the old cycle but makes every passenger
  call a car that is already there, and the UI still cannot show a countdown.
- **Restart the countdown on every boarding:** closer to a real elevator's door sensor, but a stream of
  passengers could hold the car forever; a fixed countdown keeps trips predictable.

## Consequences

- The state machine of the domain is unchanged: only the control loop decides when to close the door.
  `Idle` is now brief, since an idle car opens its door at once.
- The control loop can be woken without a request (a "nudge"), used after a return to service.
- Status messages carry one more optional field; older clients ignore it.

## Amendment (2026-10-20)

- **The doors stay open 4 s, the countdown shows 3 s.** The UI ends its countdown one second before
  `DoorsCloseAt` (the lag allowance), so a passenger who presses "Enter" or "Leave" as the bar runs out still
  reaches the server while the doors are open.
- **The countdown runs on the server's clock.** A browser's clock can be seconds away from the server's (a
  Docker Desktop VM drifted 12 s ahead), which kept the bar full until the doors closed. The UI learns the
  offset from each status's `TakenAt`, keeping the largest sample and forgetting it on reconnection, and
  compares `DoorsCloseAt` with the server's time.
