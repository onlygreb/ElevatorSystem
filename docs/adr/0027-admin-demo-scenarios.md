# 0027. Admins can start demo scenarios with simulated passengers

- **Status:** Accepted (amended by [0031](0031-huge-load-scenario-and-fast-forward.md): a fourth scenario, Huge load)
- **Date:** 2026-10-20
- **Amends:** [0017](0017-manual-passengers-occupancy-and-admin-service-control.md) §3 and §4

## Context

ADR-0017 made automated traffic a test tool, off by default and out of the admin's reach: the admin only
takes the car out of service and returns it. To show the system working (people waiting on floors, the
car filling up, riders getting off where they asked) someone has to play many passengers at once. The
product wants the admin view to offer that at the press of a button.

## Decision

1. **Three demo scenarios**, all starting every simulated passenger on the lowest floor:
   - **Morning rush:** a dozen people arrive within about fifteen seconds and ride up to their floors.
   - **Lunch break:** eight people ride up, stay a while, ride back to the lobby and leave.
   - **Busy day:** a stream of eighteen people over about a minute, each visiting one to three floors
     before leaving from the lobby.
2. **Simulated passengers are passengers** (ADR-0026): they arrive, call, walk in, choose a floor and walk
   out through the same `PassengerService` and registry rules as people, at a human pace (they look around
   four times a second and take a moment to press a button). A full car leaves them waiting; they press
   again. Their activities are marked `Simulated`.
3. **Admins start and stop them**: `POST /api/v1/admin/scenarios` and `POST /api/v1/admin/scenarios/stop`,
   under the `service-control` policy (admin, second factor), with antiforgery and an audit entry written
   before the command is sent (ADR-0023). Stopping removes every simulated passenger at once. Scenarios
   travel to the engine as `ScenarioCommandV1`.
4. **Off unless enabled**: `Simulation:Enabled` must be true in both the API and the engine. Development
   settings and the AppHost turn it on; a production deployment decides. Disabled, the API answers `409`.

## Alternatives considered

- **Keep traffic as a command-line test tool only:** it cannot be watched in the admin view, which is the
  point of a demo.
- **Simulate in the browser:** every viewer would see a different building, and the engine's counts would
  not include the simulated people.

## Consequences

- An admin can now cause elevator movement, which ADR-0017 had excluded; it is limited to these scenarios,
  audited, and can be switched off per deployment.
- The console demo runs the same scenarios (`--scenario morning|lunch|busy`), and the tests drive whole
  scenarios in simulated time.
