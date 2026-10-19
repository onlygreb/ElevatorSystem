---
name: api-backend
description: Conventions for ElevatorSystem.Api - Minimal API endpoint checklist, versioned routes, validation, ProblemDetails errors, authentication and authorization policies, rate limiting, idempotency, security headers, the SignalR hub and OpenAPI. Use when adding or changing any HTTP endpoint, middleware, hub, or the API host configuration.
---

# API backend

Decisions behind this skill: ADR-0002 (layers), ADR-0006 (UI hosting and auth), ADR-0007 (fail-safe),
ADR-0012 (control loop) and ADR-0015 (real-time fan-out).

## Shape

| Path | Role |
|---|---|
| `Program.cs` | Composition and the middleware pipeline, nothing else |
| `Hosting/` | The elevator core in-process (phase 2): controller, control loop service, readiness check |
| `RealTime/` | `ElevatorEventBuffer` (the controller's sink), `ElevatorBroadcastService`, `ElevatorStatusHub` |
| `Endpoints/` | `<Feature>Endpoints.cs` with one `Map<Feature>Endpoints(this IEndpointRouteBuilder)`; `ApiErrorHandling` |
| `Security/` | `SecurityHeaders`, `RateLimiting` |
| `ContractsJson.cs`, `ApiLog.cs` | The wire format applied to every serializer; `LoggerMessage` methods (event IDs 3000–3999) |

- Routes come from `ElevatorSystem.Contracts.ApiRoutes`, never string literals. The contracts are
  versioned: a breaking change means new types and a new route version, never an in-place edit.
- Endpoints talk to `ElevatorController` (later, Application use cases) and return `Contracts` types,
  mapped with `ElevatorContractMapper`. They never return domain types.
- Phase 2 hosts the control loop in the API (`ElevatorControlLoopService`, options from the `Elevator`
  section). Phase 3 moves it to the engine behind RabbitMQ; the endpoints keep their contract.

## Routes (v1)

| Method and route | Policy | Success | Refusals |
|---|---|---|---|
| `POST /api/v1/hall-calls` (`HallCallRequest`) | anonymous, `passenger` limit, 1 KB body | `202` `RequestAcceptedResponse` | `400` `InvalidFloor` / `InvalidDirection`, `409` `OutOfService`, `503` `Busy` + `Retry-After: 1`, `429` |
| `POST /api/v1/car-calls` (`CarCallRequest`) | anonymous, `passenger` limit, 1 KB body | `202` `RequestAcceptedResponse` | `400` `InvalidFloor`, `409` `OutOfService`, `503` `Busy` + `Retry-After: 1`, `429` |
| `GET /api/v1/elevator` | anonymous, `read` limit | `200` `ElevatorStatusDto` | `429` |
| `GET /api/v1/elevator/configuration` | anonymous, `read` limit | `200` `ElevatorConfigurationDto` | `429` |
| `/hubs/elevator` (SignalR) | anonymous, receive-only | `StatusChanged` on connect and after changes, `ActivityOccurred` per event | — |

Planned: `/api/v1/admin/*` (operators and admins, phase 4) and `/api/v1/me` (the caller's own data
for LGPD rights, phase 5).

## Checklist for every endpoint

1. Typed results (`Results<Accepted<T>, ProblemHttpResult>`) so OpenAPI is accurate.
2. Explicit `.AllowAnonymous()` (until phase 4 adds policies) together with a rate-limit policy.
3. Input validated at the boundary: the JSON options refuse malformed bodies, missing fields,
   numbers sent as strings and unknown enum names (`400`, never `500`); the domain refuses
   business-rule violations (`Building.Validate` → `RejectionReason`).
4. `.RequireRateLimiting(RateLimiting.PassengerPolicy | ReadPolicy)`; POSTs also get the 1 KB
   request size limit.
5. Refusals are `TypedResults.Problem` with the `reason` extension (`ApiRoutes.RejectionReasonExtension`)
   holding the `RejectionReason` name. Never return exception messages or stack traces.
6. OpenAPI metadata (`WithName`, `WithSummary`, `WithTags`, `ProducesProblem` for every refusal
   status); update `tests/ElevatorSystem.Api.Tests/OpenApi/openapi-v1.verified.json` in the same change.
7. A `CancellationToken` parameter, passed all the way down.
8. Phase 4: every POST that creates staff work honours an `Idempotency-Key` header, and a row is added
   to the authorization-matrix test (endpoint × anonymous/Operator/Admin/Auditor).

## Status codes

| Situation | Status |
|---|---|
| Request accepted and queued | `202 Accepted` + request ID (progress is reported on the hub) |
| Malformed JSON, missing field, unknown enum name | `400` problem, no `reason` |
| Floor outside 1–10, no travel possible in that direction | `400` problem, `reason` `InvalidFloor` / `InvalidDirection` |
| Not authenticated / not allowed (phase 4) | `401` / `403` |
| Elevator out of service | `409` problem, `reason` `OutOfService` |
| Body over 1 KB | `413` (Kestrel enforces it; the test server does not, so tests check the metadata) |
| Rate or concurrency limit hit | `429` problem + `Retry-After` |
| Controller intake full | `503` problem, `reason` `Busy`, `Retry-After: 1` |
| Engine or broker unavailable (phase 3) | `503` + `Retry-After` (never claim "accepted" without a publisher confirm) |

## Errors

- `AddApiProblemDetails` adds a `traceId` to every problem.
- `UseErrorHandling` keeps the page handlers (`/Error`, `/not-found` re-execution) outermost, and
  inside them, for any path under `/api`, the problem-details exception handler (outside Development)
  and `UseStatusCodePages`. So 404, 405, 415, malformed JSON and unhandled exceptions under `/api` are
  problems, and pages keep their HTML. Don't move the page handlers into a `UseWhen` branch:
  re-executing a page needs the top-level pipeline.
- Known routing quirk: `GET` on a POST-only route answers `404`, not `405`, because the UI's
  `{**path:file}` fallback is a GET candidate for every path. Other methods get `405`.

## JSON wire format

- `ContractsJson.Apply` inserts `ContractsJsonContext` first in the resolver chain and adds
  `JsonStringEnumConverter`. It is applied to the Minimal API options **and** the SignalR JSON protocol.
- Minimal API input is strict: `RespectRequiredConstructorParameters = true` and
  `NumberHandling = Strict`.
- The browser client configures its SignalR connection with `AddJsonProtocol` and
  `ContractsJsonContext.Default` first in `PayloadSerializerOptions.TypeInfoResolverChain`.

## Real-time hub (`/hubs/elevator`)

- `ElevatorStatusHub : Hub<IElevatorClient>`, anonymous and **receive-only**: no client-callable
  methods. Commands always go through REST, so validation and rate limits are enforced in exactly one place.
- It sends `StatusChanged` to a client as soon as it connects.
- `MaximumReceiveMessageSize` is 4 KB (clients send only the handshake and pings); detailed errors are off.
- Fan-out (ADR-0015): the controller publishes into `ElevatorEventBuffer` (bounded, `DropOldest`,
  never blocks; drops counted and logged at most every 10 s). `ElevatorBroadcastService` sends each
  event as `ActivityOccurred` and, per batch of up to 64, one `StatusChanged` only when the version
  changed. A failing send is logged and skipped. Never publish to SignalR from the control loop.
- Phase 4: admin-only hub groups require the `CanOperate` policy. The hub has no per-client
  connection limit yet (threat model OQ-11).

## Abuse protection and headers

- Rate limiting (`Security/RateLimiting.cs`, section `RateLimiting`):
  - `passenger`: token bucket per client, burst 20, then 10 per second
  - `read`: token bucket per client, burst 60, then 30 per second
  - a global concurrency limiter for everything under `/api` (256 in flight, no queue)
  - partitioned by client address, IPv6 by /64; the address is used only in memory and is never stored or logged
  - rejections are `429` problems with `Retry-After` from the limiter (default 1 s)
  - phase 4: per user for authenticated calls; forwarded headers stay off until OQ-10 is decided
- Security headers (`Security/SecurityHeaders.cs`), written in `OnStarting` on every response:
  - `Content-Security-Policy: default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'`
    (only the Development-only `/scalar` pages also allow inline scripts)
  - `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy: same-origin`,
    and a `Permissions-Policy` that disables every browser feature
  - HSTS outside Development
- The server header is off. `AllowedHosts` must be set per environment (not `*` in production).
- The OpenAPI document (`/openapi/v1.json`) and the Scalar reference (`/scalar`) are mapped in Development only.

## Logging

Don't log request bodies, headers, cookies or IP addresses; `LogPrivacyTests` captures every category at
`Trace` and fails if a client address or a body value appears. The audit log (phase 4) records staff
actions with a pseudonymised IP (see the `security-lgpd` skill). Use `LoggerMessage` methods in
`ApiLog`, event IDs 3000–3999.

## Tests (`tests/ElevatorSystem.Api.Tests`)

- `ApiFactory` runs the real host on the test server with 20 ms timings, high rate limits, in-memory
  logs (`GetFakeLogCollector()`), optional `FakeTimeProvider` (`WithFakeTime`), `WithoutControlLoop`,
  and the `X-Test-Client-Address` header to choose the client address.
- `HubClient` connects over WebSockets through the test server with the browser's JSON settings.
- `FakeHubContext` replaces the hub context to make sends hang or throw.
