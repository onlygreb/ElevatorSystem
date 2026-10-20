# 0028. Measure the elevator with OpenTelemetry metrics from its own events

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

The service defaults (ADR-0011) already export ASP.NET Core, HTTP client and runtime metrics, and the
messaging adapter traces every command across the broker. None of that answers the questions an operator
of an elevator asks: how long do people wait, how often is the car refused or interrupted, how far does it
travel, how many people are in the building right now. Those answers live in the events the controller and
the passenger registry already publish to every subscriber (ADR-0012, ADR-0026).

Any measurement must keep two existing rules: the control loop never waits for a subscriber (ADR-0015), and
nothing that identifies a passenger reaches telemetry (ADR-0024).

## Decision

1. **One meter, `ElevatorSystem.Elevator`,** owned by `ElevatorMetrics` in the Application layer. It is
   one more `IElevatorEventSink` in the engine's composite sink and one more `IPassengerObserver`, so it
   sees exactly the events that drive the activity feed, and only records numbers: it never blocks.
2. **Instruments:**

   | Name | Type | Unit | Tags |
   |---|---|---|---|
   | `elevator.requests` | counter | `{request}` | `elevator.call.kind` (hall, car), `elevator.request.outcome` (queued, merged, rejected), `elevator.request.rejection_reason` |
   | `elevator.call.wait_time` | histogram | s | `elevator.call.kind` |
   | `elevator.floors_travelled` | counter | `{floor}` | `elevator.direction` (up, down) |
   | `elevator.door.openings` | counter | `{opening}` | |
   | `elevator.out_of_service` | counter | `{interruption}` | `elevator.out_of_service.reason` |
   | `elevator.calls.cancelled` | counter | `{call}` | |
   | `elevator.passenger.steps` | counter | `{step}` | `elevator.passenger.step`, `elevator.passenger.outcome` (done, refused), `elevator.passenger.refusal_reason`, `elevator.passenger.simulated` |
   | `elevator.passenger.wait_time` | histogram | s | `elevator.passenger.simulated` |
   | `elevator.floor` | gauge | `{floor}` | |
   | `elevator.passengers` | gauge | `{passenger}` | `elevator.passenger.location` (in_car, waiting, on_floor) |

   Wait-time histograms use buckets from 0.5 s to 5 min, which suit a ten-floor building.
3. **Tags are closed sets.** No passenger id, request id or command id is ever a tag, and a test enforces
   it. Simulated passengers are tagged, so demo traffic can be told apart from real riders.
4. **Gauges report only while leading.** When a term ends, after the car has parked, the metrics forget the
   floor and the counts, so a standby engine reports nothing rather than a stale floor. Counters are
   per process and keep counting across terms, as OpenTelemetry expects of cumulative counters.
5. **Hosts opt in.** The engine host and the API in single-process mode add the meter to OpenTelemetry;
   the engine is built with metrics whenever the host provides an `IMeterFactory` (every .NET host does).
   The Aspire dashboard shows them locally; any OTLP backend receives them through the existing exporter.

## Alternatives considered

- **Metrics inside `ElevatorController`** — puts measurement in the control loop, couples the core to
  OpenTelemetry's types, and duplicates the event stream that already exists.
- **Deriving metrics from logs** — the log messages are for people; parsing them is brittle, and they are
  rate-limited in places (dropped updates are logged once per interval).
- **Prometheus scraping endpoint** — the stack already exports OTLP to the dashboard; a second protocol
  and an extra public endpoint add surface for no gain.
- **Per-floor tags on every instrument** — tempting for heat maps, but it multiplies series for every
  floor; the occupancy gauge and the activity feed already give per-floor detail.

## Consequences

- Operators get wait times, refusals, interruptions, travel and occupancy in the dashboard without new
  infrastructure.
- A new event type needs a decision in `ElevatorMetrics` too; the switch ignores unknown events, so
  forgetting is harmless but silent.
- Instrument names and tags are now an interface for dashboards and alerts: renaming one is a breaking
  change for whoever built on them.
