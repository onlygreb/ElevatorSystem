---
name: elevator-domain
description: Rules for the elevator domain and ElevatorController - states and transitions, FIFO scheduling behaviour, request validation, the thread-safety model, the control loop, the trip watchdog and fail-safe behaviour. Use when creating or changing anything in ElevatorSystem.Domain or ElevatorSystem.Application, or when reasoning about how the elevator behaves.
---

# Elevator domain

Decisions behind this skill: ADR-0004 (concurrency and time), ADR-0005 (FIFO), ADR-0007 (fail-safe)
and ADR-0012 (control loop and core semantics).

## Requirements this layer owns

| Requirement | Implemented by |
|---|---|
| One elevator serving floors 1–10 | `Building` (`Default`, `Contains`, `Validate`, `CanTravel`), `Floor` |
| States `IDLE`, `MOVING_UP`, `MOVING_DOWN`, `DOOR_OPEN` | `ElevatorState` (+ `OutOfService` with an `OutOfServiceReason`) |
| Pickup requests (floor + direction) and destination requests | `HallCall`, `CarCall` (both `ElevatorCall`); `PassengerRequest` adds id and time |
| FIFO scheduling | `FifoSchedulingStrategy` behind `ISchedulingStrategy` (`LookSchedulingStrategy` for comparison) |
| Thread-safe handling of concurrent requests | `ElevatorController` intake channel + `Elevator` lock + `ElevatorSnapshot` |
| Logging of elevator actions | Domain events, logged by `ElevatorControllerLog` (event IDs 1500–1999) |
| Invalid floor requests handled gracefully | `Building.Validate` → `RequestRejectionReason`, never an exception |
| Timeout for a stuck elevator | The trip watchdog in `ElevatorController` (`ElevatorControllerOptions.StuckTimeout`) |

## Names are part of the contract

Keep these exact names; they map one-to-one to the system requirements:

- `Elevator`: `CurrentFloor`, `State`, `TargetFloors`, `MoveUp()`, `MoveDown()`, `OpenDoor()`, `CloseDoor()`, `AddRequest(...)`
- `ElevatorController`: `Elevator`, `RequestElevator(floor, direction)`, `ProcessRequests(...)`, plus
  `RequestDestination(floor)`, `EmergencyStop()`, `StartMaintenance()`, `ReturnToService()`

## State machine

`MoveUp()`/`MoveDown()` commit the car to the adjacent floor. `CurrentFloor` then reports the floor
being travelled to, and the drive performs the trip.

| From ↓ / command → | `MoveUp` | `MoveDown` | `OpenDoor` | `CloseDoor` |
|---|---|---|---|---|
| `Idle` | `MovingUp`, floor + 1 | `MovingDown`, floor − 1 | `DoorOpen` | no-op |
| `MovingUp` | `MovingUp`, floor + 1 | refused | `DoorOpen` (stops at this floor) | no-op |
| `MovingDown` | refused | `MovingDown`, floor − 1 | `DoorOpen` (stops at this floor) | no-op |
| `DoorOpen` | refused | refused | no-op | `Idle` |
| `OutOfService` | refused | refused | refused | refused |

- `MoveUp` at the top floor and `MoveDown` at the bottom floor are refused (`AtTopFloor`, `AtBottomFloor`).
- A direction change always passes through `DoorOpen` → `CloseDoor` → `Idle`.
- `TakeOutOfService(reason)` is allowed from every state and **cancels every pending call** (they
  are listed in `ElevatorTakenOutOfService.CancelledCalls`). `ReturnToService()` goes to `Idle`
  with the door closed, and only from `OutOfService`.
- Refused transitions return a `TransitionResult` with a `TransitionError`. They never throw and
  never change state or `Version`.

**Invariants** (checked after every step by `ElevatorInvariantTests`):
1. The car never leaves the building.
2. The car never moves while the door is open or while it's out of service, and each move covers exactly one floor.
3. Pending calls are unique, valid, and at most `Building.MaxPendingCalls` (28).
4. An out-of-service car holds no calls, and no call waits at a floor whose door is open.
5. Every accepted request is pending, served or cancelled: exactly once.

## Door policy (ADR-0025)

- With `HoldDoorOpenWhenIdle` (the default), a car with nowhere to go waits with its door **open** and no
  countdown: at start-up, after `ReturnToService`, and when a stop leaves nothing pending.
- A call that gives it somewhere to go starts the `DoorOpenDuration` countdown (4 s); then the door closes
  and the car leaves. `ElevatorController.DoorsCloseAt` (and `ElevatorStatusDto.DoorsCloseAt`) is the
  close time while a countdown runs, `null` otherwise.
- The state machine is unchanged; only the control loop decides when to close. `ReturnToService` nudges
  the loop so the door opens at once.
- `ControllerHarness.DefaultOptions` pins the classic cycle (`HoldDoorOpenWhenIdle = false`) for the
  behaviour suites; `ElevatorControllerDoorTests` covers the default. `StepsAsync` lets time pass when no
  event is expected.

## FIFO behaviour

- Pending calls are served strictly in arrival order. The oldest call is the target, and the car
  travels to it **without stopping at other pending floors**.
- Opening the door at a floor **serves every pending call for that floor** (hall calls in either
  direction and car calls). A call for the floor where the door is already open is served on
  arrival (`AddRequestOutcome.ServedImmediately`).
- A request equal to one already pending (same floor and same kind/direction) is merged into it.
  It keeps its original position, and only `RequestCount` grows. Merged request IDs are not stored.

## Concurrency model

- `RequestElevator`/`RequestDestination` can be called from any thread. They validate and then
  `TryWrite` to a bounded `Channel<T>` (`SingleReader = true`). A full channel is refused as `Busy`;
  the caller is never blocked.
- `ProcessRequests(CancellationToken)` is the **only** consumer. It is an event loop that sleeps
  until a request arrives, the current trip or door cycle ends, or shutdown, so requests are
  applied **even while the car is travelling**.
- `Elevator` guards its state with `System.Threading.Lock`, and each transition is check-and-set
  inside it. Safety commands go straight to the elevator's lock and cancel the operation in flight.
- Readers get an immutable `ElevatorSnapshot`.
- Inside the lock: no `await`, no I/O, no logging, no publishing. Transitions *return* their events.
- The loop publishes each iteration's events **after** starting the next operation, so an observer
  always finds the next timer scheduled. The simulated-time tests rely on this.
- Time comes only from `TimeProvider`.

## Fail-safe behaviour

- **Trip watchdog:** every trip is awaited with `StuckTimeout` (`TravelTimePerFloor × StuckTolerance`).
  A timeout or a drive failure leads to `TakeOutOfService(Fault)`, and new requests are refused as
  `OutOfService` until `ReturnToService`.
- **Containment:**
  - a failing request is logged and dropped
  - a failing event sink is logged and ignored
  - any other loop failure takes the car out of service with `Fault`, and the loop keeps running
- **Event sinks** (`IElevatorEventSink`) must be thread-safe and fast; never do I/O inline.

## Passengers and occupancy

Decisions: ADR-0026 (passengers walk in and out themselves) and ADR-0027 (demo scenarios).
Code: `Application/Passengers` (`PassengerRegistry`, `PassengerService`, `PassengerOptions`,
`PassengerEvent`s, `IPassengerObserver`) and `Application/Simulation/DemoScenarioRunner`.
A passenger is a random id the browser keeps; nothing identifies a person (ADR-0024).

**Rules** (`PassengerRegistry`, an `IElevatorEventSink`, one lock, checked against `Elevator.Snapshot()`):

- `Join`: arrive on the lowest floor, present. Again with the same id: no change. Refused
  `BuildingFull` beyond `MaxPassengers` (300).
- `Call(direction)`: on a floor, in a direction the building allows, car in service; the passenger is
  now waiting. The caller places the hall call (`PassengerService`, or the engine's handler) and calls
  `WithdrawCall` if the controller refuses it.
- `Board`: door open on their floor (`DoorNotOpenHere`) and fewer than `CarCapacity` (8) riders
  (`CarFull`). Records the wait from the call.
- `ChooseFloor(floor)`: inside the car (`NotInCar`); the caller places the car call.
- `Leave`: inside, door open (`DoorClosed`); they are now present on that floor. A ride counts when
  they leave on another floor than the one they boarded.
- Whoever does not leave rides on. Waiting passengers keep waiting when the car leaves without them.
- `ElevatorTakenOutOfService`: riders walk out where the car stopped. People idle for `IdleTimeout`
  (30 min) are removed on the next `DoorsOpened`; simulated passengers stay until their scenario ends.
- Counts (`Current`): waiting/present per floor, riding, rides, average wait. Calls without a passenger
  move the car but count nobody.

**Wiring**: the registry comes first in the controller's `CompositeElevatorEventSink`. The observer
hears every `PassengerEvent` and every change of the counts in order, under the lock: hand them to a
buffer (the engine turns them into `ElevatorUpdateV1` and `OccupancyUpdateV1`). Event IDs 1840–1859.

**Demo scenarios** (`DemoScenarioRunner`, `SimulationOptions`, off unless `Enabled`): morning rush,
lunch break, busy day. Simulated passengers arrive on the lowest floor and use `PassengerService` like
people, at a human pace, on a `TimeProvider` timer (`TickInterval`), seeded for reproducible runs.
`StopAll` removes them. `DemoScenarioTests` drive whole scenarios in simulated time.

## Before you finish a change here

- [ ] Transition-table rows cover every (state, command) pair that changed
- [ ] Property-based invariants still hold; add a regression test for any seed that found a bug
- [ ] Concurrency tests still pass (no lost or duplicated requests; p99 assignment under 100 ms)
- [ ] The names above are unchanged; ADR-0005/0007/0012 updated if the behaviour changed
