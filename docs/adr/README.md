# Architecture Decision Records

Each significant decision is recorded as a short, immutable document: its context, the decision,
the alternatives considered and the consequences. To change a decision, add a new ADR that
supersedes the old one instead of editing it. New records start from [`template.md`](template.md).

| # | Decision | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-layered-architecture-with-ports-and-adapters.md) | Layered architecture with ports and adapters | Accepted |
| [0003](0003-rabbitmq-as-message-broker.md) | RabbitMQ as the message broker | Accepted |
| [0004](0004-concurrency-model-and-time-abstraction.md) | Concurrency model and time abstraction | Accepted |
| [0005](0005-fifo-scheduling-semantics.md) | FIFO scheduling semantics | Accepted |
| [0006](0006-blazor-webassembly-ui-hosted-by-the-api.md) | Blazor WebAssembly UI hosted by the API | Accepted |
| [0007](0007-fail-safe-operation.md) | Fail-safe operation | Accepted |
| [0008](0008-dependency-and-supply-chain-policy.md) | Dependency and supply-chain policy | Accepted |
| [0009](0009-privacy-by-design-for-lgpd.md) | Privacy by design for LGPD | Superseded by [0024](0024-drop-lgpd-compliance-scope.md) |
| [0010](0010-testing-strategy.md) | Testing strategy | Accepted |
| [0011](0011-orchestration-and-delivery.md) | Local orchestration and container delivery | Accepted |
| [0012](0012-elevator-control-loop.md) | Elevator control loop and core semantics | Accepted |
| [0013](0013-ui-culture-preference-in-browser-storage.md) | Keep the UI culture preference in browser storage (amends 0009) | Accepted |
| [0014](0014-audit-tombstones-and-chain-checkpoints.md) | Anonymise audit entries with salted personal digests and purge with checkpoints | Accepted |
| [0015](0015-real-time-fan-out-through-a-lossy-buffer.md) | Real-time fan-out through a lossy buffer | Accepted |
| [0016](0016-keep-the-browser-view-in-step-with-the-server.md) | Keep the browser view in step with the server | Accepted |
| [0017](0017-manual-passengers-occupancy-and-admin-service-control.md) | Manual passengers, occupancy counts and admin service control | Accepted |
| [0018](0018-rabbitmq-messaging-adapter.md) | RabbitMQ messaging adapter: delivery, recovery and update fan-out | Accepted |
| [0019](0019-postgresql-snapshots-deduplication-and-leader-lease.md) | Store snapshots, processed commands and the leader lease in PostgreSQL | Accepted |
| [0020](0020-occupancy-counting-rules-and-traffic-generator.md) | Count passengers by explicit rules and mirror them in the traffic generator | Superseded by [0026](0026-passengers-walk-in-and-out.md) |
| [0021](0021-engine-hosting-topology-and-single-process-mode.md) | Engine hosting topology and the single-process mode | Accepted |
| [0022](0022-staff-authentication-and-authorization.md) | Authenticate staff with Identity cookies and a mandatory second factor | Accepted |
| [0023](0023-audit-trail-writer-and-verification.md) | The API writes the audit trail, before acting, as one hash chain | Accepted |
| [0024](0024-drop-lgpd-compliance-scope.md) | Keep privacy by design, without an LGPD compliance programme | Accepted |
| [0025](0025-doors-wait-open-when-idle.md) | The car waits with its door open, and a call starts the door countdown | Accepted |
| [0026](0026-passengers-walk-in-and-out.md) | Passengers walk in and out of the car themselves, and are counted as they move | Accepted |
| [0027](0027-admin-demo-scenarios.md) | Admins can start demo scenarios with simulated passengers | Accepted |
| [0028](0028-elevator-metrics.md) | Measure the elevator with OpenTelemetry metrics from its own events | Accepted |
| [0029](0029-docker-compose-and-dockerfile.md) | Run the whole system with Docker Compose, built by a Dockerfile | Accepted |
| [0030](0030-test-account-without-second-factor.md) | A configured test account may sign in without a second factor | Accepted |
| [0031](0031-huge-load-scenario-and-fast-forward.md) | A huge load scenario that fast-forwards the building | Accepted |
| [0032](0032-stop-and-clear-empties-the-queue.md) | Stop and clear empties the queue and sends the car to the lobby | Accepted |
| [0033](0033-admins-switch-scheduling-out-of-service.md) | Admins switch between FIFO and LOOK while the car is out of service | Accepted |
| [0034](0034-docker-compose-starts-with-no-setup.md) | Docker Compose starts with no setup | Accepted |
