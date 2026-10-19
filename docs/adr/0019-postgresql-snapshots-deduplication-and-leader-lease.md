# 0019. Store snapshots, processed commands and the leader lease in PostgreSQL

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

ADR-0007 decides that every transition persists a snapshot, that a PostgreSQL advisory lock elects
a single engine, and that redelivered commands are applied once. The Application layer defines the
ports: `IElevatorStateStore`, `IProcessedCommandStore` and `ILeaderElector` with its
`ILeadershipLease`. Implementing them on PostgreSQL raised questions those records leave open:

- Saves arrive from a background writer and can overtake each other. How does a stale snapshot
  never replace a newer one?
- How long must a processed command ID be remembered, and who deletes it?
- An advisory lock lives as long as its database session. When the session ends, the server frees
  the lock at once, but the old leader only learns about it on its next round trip. How do we keep
  two engines from acting at the same time in that window?
- What frees the lock when the leader's host disappears without closing its connection?

## Decision

1. **One schema, explicit models.** The tables live in the `elevator` schema and are mapped from
   persistence classes, never from Domain types. States, directions, reasons and call kinds are
   stored as fixed lowercase codes (`moving_up`, `hall`, …) guarded by check constraints, so renaming
   a Domain member cannot change what stored rows mean. Migrations are created with the local
   `dotnet-ef` tool and kept as generated code.
2. **The snapshot is one row plus its calls.** `elevator_state` holds floor, state, travel direction,
   out-of-service reason, version and time; `elevator_pending_call` holds the calls in FIFO order with
   a kind discriminator, the first request's ID and time, and the merged request count. A save is
   one conditional upsert, `INSERT … ON CONFLICT (id) DO UPDATE … WHERE version < EXCLUDED.version`,
   and the calls are rewritten in the same transaction only when that statement wrote the row. The
   upsert's row lock serialises concurrent saves, so the highest version always wins. An equal
   version is ignored.
3. **Processed command IDs are kept for 7 days.** The ID is the primary key and the insert uses
   `ON CONFLICT DO NOTHING`, so a duplicate returns `false` without an exception. A background job
   purges expired IDs at start-up and then hourly, in batches of 1,000. The purge is idempotent and
   uses `SKIP LOCKED`, so every instance may run it without leadership. The retention must outlast
   any redelivery. A command is delivered at most 5 times (the commands queue's delivery limit, in
   the `messaging` skill), normally within seconds, so 7 days leaves room even for an engine outage
   of several days.
4. **The lease is a session lock on a dedicated, unpooled connection.** A standby retries
   `pg_try_advisory_lock` every `LeaderAcquireRetryInterval` (2 s). The leader runs `SELECT 1` on
   its lock connection every `LeaderCheckInterval` (1 s), with a timeout of one interval, and the
   first failure cancels `Lost` and closes the connection. Disposing the lease cancels `Lost`, then
   unlocks and closes. The connection never comes from a pool, so the lock can never return to a pool
   with a connection.
5. **A new leader waits before it acts.** After taking the lock, an engine waits three check
   intervals (the `TakeoverDelay`) and starts only if its own lease is still valid. A previous
   leader notices a lost lock within two intervals (the wait for its next check, then that check's
   timeout), so it has stopped before the new one starts. The tests kill the leader's session with
   `pg_terminate_backend` and assert exactly this order; without the delay they fail every time.
6. **The server frees the lock of a vanished leader.** The lock session sets
   `idle_session_timeout` to five check intervals. The leader's checks keep the session busy, so
   only a leader that stopped talking (a crashed host, a broken network) is disconnected, and its
   lock is freed within seconds instead of waiting for TCP keep-alives.
7. **Hosts migrate, then compete.** A host applies migrations at start-up through
   `ElevatorDatabaseMigrator`, before it asks for leadership. EF Core takes a database lock while it
   migrates, so engines that start together are safe.
8. **An unreachable database degrades readiness.** The `elevator-database` health check is tagged
   `ready` and reports `Degraded`, not `Unhealthy`: the car keeps operating (ADR-0007).

## Alternatives considered

- **Calls as a `jsonb` column on the state row:** one statement per save, but the discriminator and
  the per-call constraints would live in application code only, and the calls could not be queried.
- **A lease row with an expiry time, renewed by heartbeat:** works without a dedicated connection,
  but its safety depends on the clocks of every engine and the database agreeing.
- **A transaction-level lock (`pg_advisory_xact_lock`):** would hold a transaction open for the
  whole term of leadership, which blocks vacuum and is fragile under connection resets.
- **An external coordinator (etcd, ZooKeeper, Redis):** another moving part to secure and operate,
  for a decision the database already in use can make.
- **Fencing tokens checked on every write:** the strongest guarantee against a leader that was
  paused (a long GC or a frozen VM) for longer than the takeover delay. Not built now: snapshot
  versions already reject stale saves, and the commands queue's single active consumer (ADR-0007)
  is a second, independent guard.

## Consequences

- Failover takes about the retry interval plus the takeover delay (around 5 s with the defaults),
  plus up to `idle_session_timeout` when the old leader vanished without closing its connection.
- The first engine to start also waits the takeover delay, about 3 s, before it drives the car.
- Every engine needs one extra database connection, for its lock, while it leads or stands by.
- The intervals are configuration (`Persistence` section). Lowering `LeaderCheckInterval` speeds up
  failover but makes a slow database more likely to cost leadership; it is validated between 50 ms
  and 1 minute.
- None of these tables holds personal data (ADR-0009); `docs/lgpd/retention.md` lists them.
