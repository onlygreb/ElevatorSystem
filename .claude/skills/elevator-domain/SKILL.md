---
name: elevator-domain
description: Rules for the elevator domain and ElevatorController - states and transitions, FIFO scheduling behaviour, request validation, the thread-safety model, the watchdog and fail-safe behaviour. Use when creating or changing anything in ElevatorSystem.Domain or ElevatorSystem.Application, or when reasoning about how the elevator behaves.
---

# Elevator domain

Decisions behind this skill: ADR-0004 (concurrency and time), ADR-0005 (FIFO), ADR-0007 (fail-safe).

## Requirements this layer owns

| Requirement | Implemented by |
|---|---|
| One elevator serving floors 1–10 | `Floor` value object (validated), `BuildingOptions` |
| States `IDLE`, `MOVING_UP`, `MOVING_DOWN`, `DOOR_OPEN` | `ElevatorState` (+ `OutOfService`, see below) |
| Pickup requests (floor + direction) and destination requests | `HallCall`, `CarCall` |
| FIFO scheduling | `FifoSchedulingStrategy` behind `ISchedulingStrategy` |
| Thread-safe handling of concurrent requests | `ElevatorController` intake channel + `Elevator` lock |
| Logging of elevator actions | Domain events, logged by the application layer |
| Invalid floor requests handled gracefully | `Floor.TryCreate` returning a result, never an exception |
| Timeout for a stuck elevator | `ElevatorWatchdog` |

## Names are part of the contract

Keep these exact names; they map one-to-one to the system requirements:

- `Elevator`: `CurrentFloor`, `State`, `TargetFloors`, `MoveUp()`, `MoveDown()`, `OpenDoor()`, `CloseDoor()`, `AddRequest(...)`
- `ElevatorController`: `Elevator`, `RequestElevator(floor, direction)`, `ProcessRequests(...)`

## State machine

Movement is modelled floor by floor: `MoveUp()` advances exactly one floor.

| From ↓ / command → | `MoveUp` | `MoveDown` | `OpenDoor` | `CloseDoor` |
|---|---|---|---|---|
| `Idle` | `MovingUp`, floor + 1 | `MovingDown`, floor − 1 | `DoorOpen` | no-op |
| `MovingUp` | `MovingUp`, floor + 1 | rejected | `DoorOpen` (stops at this floor) | no-op |
| `MovingDown` | rejected | `MovingDown`, floor − 1 | `DoorOpen` (stops at this floor) | no-op |
| `DoorOpen` | rejected | rejected | no-op | `Idle` |
| `OutOfService` | rejected | rejected | rejected | rejected |

- `MoveUp` at floor 10 and `MoveDown` at floor 1 are rejected.
- A direction change always passes through `DoorOpen` → `CloseDoor` → `Idle`.
- `TakeOutOfService(reason)` (reason: `Maintenance`, `Fault`, `EmergencyStop`) is allowed from
  every state; `ReturnToService()` goes to `Idle` with the door closed, and only from `OutOfService`.
- Rejected transitions return a failure result. They never throw and never change state.

**Invariants** (the property-based tests check these):
1. `1 ≤ CurrentFloor ≤ 10`, always.
2. The car never moves while the door is open or while it's out of service.
3. `TargetFloors` contains no duplicates.
4. Each `MoveUp`/`MoveDown` changes the floor by exactly one.

## FIFO behaviour

- Pending requests are served strictly in arrival order. The head of the queue is the target, and
  the car travels to it **without stopping at other pending floors**. That is what FIFO means
  here; `LookSchedulingStrategy` exists to show the alternative.
- A request equal to one already pending (same floor and same kind/direction) is merged into it
  and keeps its original position.
- A hall call at the current floor while `Idle` opens the door straight away.
- The pending set is bounded: at most 18 hall calls (no "up" at 10, no "down" at 1) plus 10 car
  calls. Memory stays flat however many requests arrive.

## Concurrency model

- `RequestElevator` can be called from any thread. It validates input and writes to a bounded
  `Channel<T>` (`SingleReader = true`). If the channel is full, the request is rejected with a
  clear "busy" result. Never block the caller indefinitely.
- `ProcessRequests(CancellationToken)` is the **only** consumer and the only code that changes the queue.
- `Elevator` guards its state with a `System.Threading.Lock`. Each transition is check-and-set
  inside the lock. Safety commands (emergency stop, maintenance) skip the channel and take the
  lock directly, so they pre-empt queued work.
- Readers get an immutable `ElevatorSnapshot` record taken inside the lock. The API and UI only
  ever see snapshots.
- Inside the lock: no `await`, no I/O, no logging and no event publishing. Collect the events,
  release the lock, then publish them.
- Time comes from `TimeProvider` only. Delays use `Task.Delay(delay, timeProvider, token)`.

## Fail-safe behaviour

- **Watchdog:** if the car makes no progress within `TravelTimePerFloor × StuckTolerance`, it calls
  `TakeOutOfService(Fault)`, raises `ElevatorFaulted`, and rejects new requests with a reason
  until an operator returns it to service.
- **The control loop never waits on I/O.** Events go to an `IElevatorEventSink` backed by a
  bounded buffer, so a slow broker or database can't stall the elevator.
- **One failing command doesn't stop the loop.** Its exception is caught, logged and the command
  rejected. A truly unhandled failure stops the host, the orchestrator restarts it, and state is
  restored from the last snapshot (phase 3).

## Logging

Use `LoggerMessage` source-generated methods with stable event IDs: 1000–1499 for the domain,
1500–1999 for the application layer. Log floors, states and request IDs. There is no personal
data in this layer, so there is nothing personal to log.

## Before you finish a change here

- [ ] Transition-table tests cover every (state, command) pair that changed
- [ ] Property-based invariants still hold; add a regression test for any seed that found a bug
- [ ] Concurrency test still passes (no lost or duplicated requests under parallel producers)
- [ ] The names above are unchanged; ADR-0005/0007 updated if the behaviour changed
