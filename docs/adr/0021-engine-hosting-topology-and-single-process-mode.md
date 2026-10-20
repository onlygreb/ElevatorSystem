# 0021. Engine hosting topology and the single-process mode

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

ADR-0003, ADR-0007, ADR-0018 and ADR-0019 put RabbitMQ between the API and the engine, PostgreSQL behind
the engine, and an advisory lock in front of the car. Phase 2 still ran the controller inside the API.
Moving it into the engine raised questions those records leave open:

- How is the engine's cycle structured, so that losing leadership, a crash and a graceful stop each end safely?
- How do API instances learn the elevator's state, including one that starts after the engine went quiet?
- `ElevatorUpdateV1` always carries an activity. How does a restored state reach clients when nothing happened?
- Snapshots are saved in the background. How do browsers, which ignore a status whose version is not higher
  than the one shown (ADR-0016), accept the state of a new leader?
- A command must be applied once, but the deduplication store and the snapshot are not one transaction.
- How do development and the API tests keep running without Docker?

## Decision

1. **One cycle per engine** (`ElevatorEngine`, `Application/Hosting`). The engine host first migrates in a
   hosted service registered ahead of the persistence services, so the schema exists before the retention job
   or the engine use it (ADR-0019 §7; it retries while the database is down). Then the engine repeats:
   acquire the lease, load the snapshot (retrying while the database is down; the car does not move without
   it), build a new `ElevatorController` for this term, drive while consuming commands.
   - **Lease lost:** stop driving, consuming and saving at once, then acquire again. Nothing is flushed:
     another engine may already be taking over.
   - **Graceful stop:** stop consuming first, so a command being handled reaches the car; stop the loop;
     park a moving car at its floor with the door open (ADR-0007 §8); save and publish within
     `Engine:ShutdownTimeout`; release the lease.
   - **Unexpected end** of the loop or the consumer: rethrown, so the host stops and is restarted.
2. **Each term opens with a door cycle** where the car stands (not for an out-of-service car). The engine
   cannot know whether anyone is inside, calls for that floor are served, and the restored state reaches every
   subscriber through a real `DoorsOpened` event with a fresh version. No contract change is needed.
3. **Events leave through two background workers.** `SnapshotPersister` saves the newest snapshot, collapsing
   bursts and retrying while the database is down. `ElevatorUpdateForwarder` publishes an `ElevatorUpdateV1`
   per event and an `OccupancyUpdateV1` per change of the counts from a bounded, drop-oldest buffer. The
   control loop never waits for either (ADR-0007 §4).
4. **Saved before shown.** An elevator update is published only once the snapshot it reports is stored, unless
   saving is failing (then it is published anyway and readiness is `Degraded`). Every version a client has seen
   survives a crash, so the next leader's versions continue above it and browsers accept them without reconnecting.
5. **The latest updates are re-sent** with their original ids after `Engine:StatusRefreshInterval` (5 s) without
   changes. Subscribers already holding them drop the copies by id; an API that started late, or missed updates
   during a broker outage, catches up. This replaces a status-only message.
6. **Commands are marked processed before they are applied**, and acknowledged once the loop has taken them in.
   A redelivered copy is always dropped. The cost is a window of one database round trip: a crash between the
   mark and the next snapshot save loses that call, and the passenger presses again. Marking after applying
   would apply it twice instead. When the deduplication store is unreachable the command is applied anyway:
   a repeated call merges into the pending one, a dropped one strands a passenger. A refused call is reported as
   a `RequestRejected` activity, since the API already answered `202`.
7. **The API holds no elevator.** Calls are validated (`Building.Validate`, and `OutOfService` from the last known
   status), published, and answered `202` with the command id after the confirm; `MessagingUnavailableException`
   is `503` with `Retry-After: 2` and reason `Busy`. `GET elevator`, `GET building/occupancy` and the hub read an
   `ElevatorStateCache` fed by the update subscription, in arrival order with copies dropped by id; before the
   first update the reads answer `503` with `Retry-After`. Readiness has `elevator-status`, `Degraded` until then.
   Configuration comes from the API's own `Elevator` section, which must match the engine's.
8. **Two hosting modes, one code path** (`Hosting:Mode`): `Distributed` (the default, and what Aspire runs) uses
   RabbitMQ; `SingleProcess` runs the same `AddElevatorEngine` inside the API over the in-memory bus, stores and
   leader elector. Development and the API tests use it; nothing survives a restart there.
9. **Topology under Aspire:** RabbitMQ 4.1 with the management plugin, PostgreSQL 17.6 with the `elevatordb`
   database, both with data volumes; two engine replicas (leader and standby) and the API, each waiting for its
   dependencies and probed on `/health`. User names are parameters with defaults; passwords are generated
   parameters kept in the AppHost's user secrets. The engine reports its role at `/health/role` and in readiness.
10. **End-to-end tests use Testcontainers, not Aspire.Hosting.Testing.** The API runs on the test server and the
    engines run as child processes. A test needs to kill one replica for real (a crash, not a graceful stop),
    stop and restart the broker on the same port, and give each test its own virtual host and database so the
    containers can be shared. That is direct with Testcontainers and processes; through the orchestrator it would
    depend on how DCP stops a replica and recreates a container, and would start the whole graph per scenario.
    The AppHost itself is checked by running it.

## Alternatives considered

- **A status-only message, or a nullable activity:** the cleanest way to announce a restored state, but a
  contract change; the start-up door cycle and the re-sent update cover the same cases without one. If a
  later version needs it, it is an additive `V2`.
- **Jumping the version on restore:** would keep browsers accepting the new leader, but no gap is provably
  large enough under a burst; saving before showing makes the order exact.
- **The API reading snapshots from PostgreSQL:** gives late API instances the state, but adds a database
  dependency and credentials to the internet-facing service.
- **Marking commands after applying them:** never loses a call in the crash window, but applies redelivered
  calls twice, against the port's contract.
- **Keeping the controller in the API in development only:** two code paths, and the API tests would stop
  covering the distributed one.

## Consequences

- Failover takes the lease's retry interval plus the takeover delay (about 5 s by default, under a second with
  the integration tests' settings), then a 3 s door cycle before the restored calls are served.
- Every update costs one database round trip of latency; the save is coalesced, so throughput is unaffected.
- An API that starts while the engine holds an out-of-service car learns nothing until the next change, because
  there is no event to re-send. Phase 4's return to service ends that state.
- Commands are applied one at a time with a database write each, which bounds intake at a few hundred calls a
  second; phase 5's load tests measure it.
- Health endpoints are still mapped only in Development (ServiceDefaults); production exposure is decided with
  deployment in phase 5.
