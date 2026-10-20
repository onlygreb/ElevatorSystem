---
name: api-backend
description: Conventions for ElevatorSystem.Api - Minimal API endpoint checklist, versioned routes, validation, ProblemDetails errors, authentication and authorization policies, rate limiting, idempotency, security headers, the SignalR hub and OpenAPI. Use when adding or changing any HTTP endpoint, middleware, hub, or the API host configuration.
---

# API backend

Decisions behind this skill: ADR-0002 (layers), ADR-0006 (UI hosting and auth), ADR-0007 (fail-safe),
ADR-0012 (control loop), ADR-0015 (real-time fan-out), ADR-0022 (staff authentication) and ADR-0023 (audit trail).

## Shape

| Path | Role |
|---|---|
| `Program.cs` | Composition and the middleware pipeline, nothing else |
| `Hosting/` | `Hosting:Mode` (Distributed over RabbitMQ, or SingleProcess with the engine in process) and readiness checks (ADR-0021) |
| `RealTime/` | `ElevatorEventBuffer` (the controller's sink), `ElevatorBroadcastService`, `ElevatorStatusHub` |
| `Endpoints/` | `<Feature>Endpoints.cs` with one `Map<Feature>Endpoints(this IEndpointRouteBuilder)`; `ApiErrorHandling` |
| `Security/` | `SecurityHeaders`, `RateLimiting` |
| `Staff/` | Identity, cookies, policies (`StaffPolicies`), antiforgery, Data Protection, the staff start-up (migrate and seed), service-action idempotency (ADR-0022) |
| `ContractsJson.cs`, `ApiLog.cs` | The wire format applied to every serializer; `LoggerMessage` methods (event IDs 3000–3999) |

- Routes come from `ElevatorSystem.Contracts.ApiRoutes`, never string literals. The contracts are
  versioned: a breaking change means new types and a new route version, never an in-place edit.
- Endpoints publish commands through `IElevatorCommandPublisher` and read `ElevatorStateCache` (the latest
  `ElevatorUpdateV1`/`OccupancyUpdateV1` from the subscription). They return `Contracts` types, never domain types.
- The engine runs in its own processes (Distributed, the default) or inside the API over the in-memory bus
  (SingleProcess, Development and the API tests), through the same `AddElevatorEngine` (ADR-0021).

## Routes (v1)

| Method and route | Policy | Success | Refusals |
|---|---|---|---|
| `POST /api/v1/hall-calls` (`HallCallRequest`) | anonymous, `passenger` limit, 1 KB body | `202` `RequestAcceptedResponse` | `400` `InvalidFloor` / `InvalidDirection`, `409` `OutOfService`, `503` `Busy` + `Retry-After: 2` (broker unavailable), `429` |
| `POST /api/v1/car-calls` (`CarCallRequest`) | anonymous, `passenger` limit, 1 KB body | `202` `RequestAcceptedResponse` | `400` `InvalidFloor`, `409` `OutOfService`, `503` `Busy` + `Retry-After: 2` (broker unavailable), `429` |
| `GET /api/v1/elevator` | anonymous, `read` limit | `200` `ElevatorStatusDto` | `429`, `503` + `Retry-After: 2` until the engine reported |
| `GET /api/v1/building/occupancy` | `staff` policy, `staff` limit | `200` `BuildingOccupancyDto` | `401`, `403`, `429`, `503` + `Retry-After: 2` until the engine reported |
| `GET /api/v1/elevator/configuration` | anonymous, `read` limit | `200` `ElevatorConfigurationDto` | `429` |
| `/hubs/elevator` (SignalR) | anonymous, receive-only | `StatusChanged` on connect (once known) and after changes, `ActivityOccurred` per update; `OccupancyChanged` only to the `staff` group | — |
| `GET /api/v1/auth/antiforgery` | anonymous, `read` limit | `200` `AntiforgeryTokenDto` (`Cache-Control: no-store`) | `429` |
| `POST /api/v1/auth/sign-in` (`SignInRequest`) | anonymous, `sign-in` limit, antiforgery | `200` `StaffSessionDto` + session cookie | `400` `Antiforgery`, `401` `InvalidCredentials` / `TwoFactorRequired` / `InvalidTwoFactorCode`, `429` |
| `POST /api/v1/auth/sign-out` | signed in, antiforgery | `204` | `401` |
| `GET /api/v1/auth/session` | signed in | `200` `StaffSessionDto` | `401` |
| `POST /api/v1/auth/two-factor/setup` | signed in, antiforgery | `200` `TwoFactorSetupDto` | `401`, `409` already enabled |
| `POST /api/v1/auth/two-factor/enable` (`EnableTwoFactorRequest`) | signed in, antiforgery | `200` `RecoveryCodesDto`; the session now counts as two-factor | `400` `InvalidTwoFactorCode`, `401`, `409` |
| `POST /api/v1/admin/service-actions` (`ServiceActionRequest`, optional `Idempotency-Key`) | `service-control` policy, antiforgery | `202` `ServiceActionAcceptedResponse` | `400`, `401`, `403`, `422` key reused, `503` `Busy` |
| `GET /api/v1/admin/audit/entries?before=&limit=` | `staff` policy | `200` `AuditPageDto`, newest first | `400`, `401`, `403` |
| `GET /api/v1/admin/audit/verification` | `staff` policy | `200` `AuditVerificationDto` | `401`, `403` |
| `POST /api/v1/passengers` | anonymous, `passenger` limit | `202` `PassengerAcceptedResponse` (a random passenger id on floor 1) | `429`, `503` `Busy` |
| `GET /api/v1/passengers/{id}` | anonymous, `read` limit | `200` `PassengerDto` (from the activity feed) | `404` `UnknownPassenger`, `429` |
| `POST /api/v1/passengers/{id}/hall-call` (`PassengerHallCallRequest`), `/board`, `/car-call` (`PassengerCarCallRequest`), `/leave` | anonymous, `passenger` limit, 1 KB | `202` `PassengerAcceptedResponse`; the engine reports the step, or `PassengerRefused` and its reason, on the hub under the request id | `400`, `429`, `503` `Busy` |
| `POST /api/v1/admin/scenarios` (`StartScenarioRequest`), `/admin/scenarios/stop` | `service-control` policy, antiforgery, audited | `202` `ScenarioAcceptedResponse` | `400`, `401`, `403`, `409` disabled (`Simulation:Enabled`), `503` |

Policies (`Staff/StaffPolicies.cs`): `staff` is `Admin` or `Auditor` signed in with the second factor (`amr: mfa`);
`service-control` is `Admin` with the second factor. A password-only session reaches only `session` and the two-factor
endpoints. Planned: account management.

## Checklist for every endpoint

1. Typed results (`Results<Accepted<T>, ProblemHttpResult>`) so OpenAPI is accurate.
2. Explicit `.AllowAnonymous()` or `.RequireAuthorization(policy)` on the endpoint itself (never on a group: an
   `AllowAnonymous` group overrides every policy inside it), together with a rate-limit policy.
   `EveryApiEndpoint_StatesWhoMayCallIt` fails otherwise.
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
8. Staff `POST`s use `.RequireAntiforgeryToken()`; an action an admin may retry honours `Idempotency-Key`; and a row is
   added to `AuthorizationMatrixTests` (endpoint × anonymous / password-only / auditor / admin).
9. Anything a staff member does is recorded with `AuditTrail.RecordAsync` *before* it takes effect (ADR-0023).

## Status codes

| Situation | Status |
|---|---|
| Request accepted and queued | `202 Accepted` + request ID (progress is reported on the hub) |
| Malformed JSON, missing field, unknown enum name | `400` problem, no `reason` |
| Floor outside 1–10, no travel possible in that direction | `400` problem, `reason` `InvalidFloor` / `InvalidDirection` |
| Not signed in / not allowed (or no second factor) | `401` / `403` (no redirects) |
| Elevator out of service | `409` problem, `reason` `OutOfService` |
| Body over 1 KB | `413` (Kestrel enforces it; the test server does not, so tests check the metadata) |
| Rate or concurrency limit hit | `429` problem + `Retry-After` |
| Command not confirmed by the broker (or the in-memory queue full) | `503` problem, `reason` `Busy`, `Retry-After: 2` (never claim "accepted" without a confirm) |
| Engine refuses an accepted call (intake full, out of service meanwhile) | Already `202`; reported as a `RequestRejected` activity on the hub |
| Status or occupancy not reported yet | `503` problem without `reason`, `Retry-After: 2` |

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
- Fan-out (ADR-0015): the update subscription writes each new update into `ElevatorEventBuffer` (bounded,
  `DropOldest`, never blocks; drops counted and logged at most every 10 s). `ElevatorBroadcastService` sends
  each elevator update as `ActivityOccurred` and, per batch of up to 64, one `StatusChanged` only when the
  version changed and one `OccupancyChanged` with the newest counts. A failing send is logged and skipped. Never publish to SignalR from the control loop.
- Staff connections (the `staff` policy when they connect) join the `staff` group, the only audience of
  `OccupancyChanged`; `CloseOnAuthenticationExpiration` ends them with the cookie. The hub has no per-client
  connection limit yet (threat model OQ-11).

## Abuse protection and headers

- Rate limiting (`Security/RateLimiting.cs`, section `RateLimiting`):
  - `passenger`: token bucket per client, burst 20, then 10 per second
  - `read`: token bucket per client, burst 60, then 30 per second
  - a global concurrency limiter for everything under `/api` (256 in flight, no queue)
  - partitioned by client address, IPv6 by /64; the address is used only in memory and is never stored or logged
  - rejections are `429` problems with `Retry-After` from the limiter (default 1 s)
  - `sign-in`: per client, 10 attempts, then one every 6 s; `staff`: per account (per client before sign-in), burst 30, then 5 per second
  - forwarded headers stay off until OQ-10 is decided
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
`Trace` and fails if a client address or a body value appears. The audit trail (ADR-0023) records staff
actions with a pseudonymised IP (see the `security` skill). Use `LoggerMessage` methods in
`ApiLog`, event IDs 3000–3999.

## Tests (`tests/ElevatorSystem.Api.Tests`)

- `ApiFactory` runs the real host on the test server with 20 ms timings, high rate limits, in-memory
  logs (`GetFakeLogCollector()`), optional `FakeTimeProvider` (`WithFakeTime`), `WithoutControlLoop`,
  and the `X-Test-Client-Address` header to choose the client address.
- `HubClient` connects over WebSockets through the test server with the browser's JSON settings.
- `FakeHubContext` replaces the hub context to make sends hang or throw.
- `Staff/StaffClient` signs in like the browser (HTTPS, cookie jar, antiforgery token) as `StaffAccounts.Admin` or
  `.Auditor`, seeded by `ApiFactory`; `SignedInWithTwoFactorsAsync` registers an authenticator with the RFC 6238 `Totp`
  helper. `HubClient.ConnectAsync(..., cookieHeader: client.CookieHeader)` joins the hub as that session.
