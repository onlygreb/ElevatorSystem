# 0017. Manual passengers, occupancy counts and admin service control

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

The original plan imagined an operations panel where staff could also steer the elevator: change
the scheduling strategy, trigger actions, manage users. The product owner clarified the roles:

- **Passengers are people.** They call the elevator and choose their floor by hand.
- **The admin observes and manages the service, but does not operate the elevator.** The admin
  sees how many people are in the elevator, on each floor and waiting on each floor, and can take
  the elevator out of service and return it to service. The admin never places calls or moves the car.
- **Automated passengers are only a testing aid.** They exist for tests and load runs, never as a
  substitute for real users.

## Decision

1. **Passenger calls stay manual.** They come from the passenger view or the REST endpoints (the
   `requestElevator` requirement). Each call is one person.
2. **The engine counts people from the calls they place.** An `OccupancyTracker` in
   `ElevatorSystem.Application` follows the elevator's events:
   - a hall call (including a merged repeat) is one more person **waiting** on that floor, who
     stops being **present** there
   - when the door opens at a floor, the people waiting there **board** in arrival order, up to the
     car's **capacity** (default 8)
   - anyone left behind stays waiting and has to call again, as in a real lobby
   - a served car call lets that many riders **get off**; they become **present** on that floor and
     count as a delivered trip
   - average wait runs from calling to boarding
   - counts never go negative, and anything inconsistent is clamped and logged
   The counts reach clients as `BuildingOccupancyDto`, through `GET /api/v1/building/occupancy` and
   the hub's `OccupancyChanged`, and travel between processes as `OccupancyUpdateV1`.
3. **The admin has service control only.** The admin can **start maintenance**, trigger an
   **emergency stop**, and **return to service**. These travel as `ServiceCommandV1` and are applied
   ahead of queued calls (ADR-0007). The admin view is otherwise read-only: occupancy, status and
   activity. Authentication and authorization for it come in phase 4.
4. **Automated traffic is a test tool.** A `TrafficGenerator` places calls through the same path as
   people, with `TimeProvider` and a seeded random source so runs are reproducible. It drives
   property, integration and load tests. It is **off by default** and is never enabled in production
   configuration.
5. **Passengers have no identity.** Only counts exist; nothing identifies an individual, so nothing
   about them is personal data (ADR-0009).

## Alternatives considered

- **An automatic simulation as the main mode:** the product needs real users to drive it, and
  simulated traffic is only for testing.
- **Admin operating the elevator** (placing calls, changing strategy at runtime): this mixes
  operation with administration, and adds authority the role does not need.
- **Counting people with sensors per car or floor:** there is no hardware in this system. Counting
  from calls is the honest model, and the counting rules are documented here.

## Consequences

- People counts are estimates built from calls, not sensor readings. The rules above make them
  deterministic and testable.
- Phase 4 covers authentication for the admin view, the occupancy dashboard with the three service
  actions, and the tamper-evident audit log of service actions and system events.
