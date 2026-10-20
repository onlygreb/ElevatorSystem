# 0033. Admins switch between FIFO and LOOK while the car is out of service

- **Status:** Accepted
- **Date:** 2026-10-20
- **Amends:** [0005](0005-fifo-scheduling-semantics.md), [0017](0017-manual-passengers-occupancy-and-admin-service-control.md)

## Context

ADR-0005 made LOOK a configuration choice (`Elevator:Scheduling`), so comparing it with FIFO meant editing
settings and restarting the engines. Admins want to compare the two from the admin view, with the same demo
scenario, without a restart. Switching while calls are pending would leave a queue planned by one strategy and
served by the other.

## Decision

1. **Only out of service.** `ElevatorController.ChangeScheduling(policy)` switches the strategy only while the
   car is out of service, when it holds no calls (taking it out of service cancels them). The controller reads
   the strategy afresh at every decision, so the car uses the new one as soon as it returns to service.
2. **An admin action like the others.** `POST /api/v1/admin/scheduling` with `{"scheduling":"Fifo"|"Look"}`:
   - the `service-control` policy (admin with a second factor, or the test account), antiforgery and the staff
     rate limit;
   - `409` while the car is in service, judged from the API's latest status;
   - written to the audit trail (`elevator.scheduling`) before `SchedulingCommandV1` is sent, and again as
     refused if the broker is away;
   - the engine checks again: if the car went back into service meanwhile, the command is refused and logged
     (event 2022).
3. **Visible.** The status carries `Scheduling` (additive), the engine announces a `SchedulingChanged`
   activity, and the admin view's switch, enabled only while the car is out of service, shows the strategy in
   use; the average wait says which strategy it was measured under. A change of scheduling keeps the elevator's
   version, so the API sends the status again when it differs, as for the speed (ADR-0031).
4. **Until the engine restarts.** The choice lives in the leading engine's memory. A restart or a failover
   starts again with the configured `Elevator:Scheduling`, and the status shows which one is in use.

## Alternatives considered

- **Switch at any time:** pending calls would be served in an order neither strategy chose, and the switch
  would change a passenger's expected wait under their feet.
- **Persist the choice with the snapshot:** a schema change for a comparison tool; the configured default is a
  clear, documented fallback.

## Consequences

- The authorization matrix, the OpenAPI snapshot and the audit tests cover the new endpoint.
- Comparing the strategies: take the car out of service, switch, return it, run the same demo scenario, and
  read the average wait.
