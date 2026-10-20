# 0007. Fail-safe operation

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

An elevator controller must fail into a safe state. A stuck car must be detected, an
infrastructure outage must not freeze the car, a crash must not lose accepted requests, and two
controllers must never drive the same car. These are explicit requirements: timeouts for stuck
elevators and proper exception handling for concurrent operations.

## Decision

1. **Safety rules live in the domain.** Illegal transitions (moving with the door open, moving
   past the top or bottom floor, any movement while out of service) are rejected by the state
   machine itself, not by callers.
2. **There is an `OutOfService` state** (reason: `Maintenance`, `Fault`, `EmergencyStop`),
   reachable from any state. Leaving it requires an explicit `ReturnToService`.
3. **A watchdog detects a stuck car.** No progress within `TravelTimePerFloor × StuckTolerance`
   means a `Fault`: the elevator goes out of service, an alert event is raised, and new requests
   are rejected with a reason.
4. **I/O is isolated from control.** The control loop never awaits the broker or the database.
   Events leave through a bounded buffer. If dependencies are down, the car keeps operating and
   health reports `Degraded`.
5. **Errors are contained.** A request that fails to apply is logged and dropped; any other failure
   in the control loop takes the car out of service (`Fault`) and the loop continues. An unrecoverable failure stops the host
   (`BackgroundServiceExceptionBehavior.StopHost`) so the orchestrator restarts it cleanly instead
   of leaving a zombie.
6. **State is recoverable.** Every transition persists a state snapshot. On start-up the engine
   restores the car's floor and its pending requests.
7. **There is a single controller.** A PostgreSQL advisory lock elects the leader, and the
   commands queue has a single active consumer. A standby engine idles until it acquires leadership.
8. **Shutdown is graceful.** On shutdown the car completes its current floor, opens its doors and
   persists its state before the process exits.

## Alternatives considered

- **Retry everything forever:** hides failures and can leave the car in an unknown state.
  Explicit out-of-service with a reason is observable and recoverable.
- **Event sourcing for recovery:** a full audit-grade history, but more machinery than snapshot
  persistence needs. The audit log already provides history for people.

## Consequences

- Each rule is a testable scenario in the integration and chaos tests (ADR-0010).
- Operators get clear signals: an out-of-service reason, `Degraded` health, dead-letter alerts.
- The design adds a database dependency for snapshots and leadership. Without it, the engine runs
  as a single instance with in-memory state, which is the console demo's configuration.
