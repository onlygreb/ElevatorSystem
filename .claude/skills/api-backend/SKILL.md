---
name: api-backend
description: Conventions for ElevatorSystem.Api - Minimal API endpoint checklist, versioned routes, validation, ProblemDetails errors, authentication and authorization policies, rate limiting, idempotency, security headers, the SignalR hub and OpenAPI. Use when adding or changing any HTTP endpoint, middleware, hub, or the API host configuration.
---

# API backend

Decisions behind this skill: ADR-0002 (layers), ADR-0006 (UI hosting and auth), ADR-0007 (fail-safe).

## Shape

- Minimal APIs, grouped by feature in `Endpoints/<Feature>Endpoints.cs`, each exposing a single
  `Map<Feature>Endpoints(this IEndpointRouteBuilder)` method.
- Everything lives under `/api/v1`. A breaking change means a new version group, never an in-place edit.
- Planned groups:
  - `/api/v1/requests` (hall and car calls, anonymous with rate limits)
  - `/api/v1/elevators` (read-only state)
  - `/api/v1/admin/*` (operators and admins)
  - `/api/v1/me` (the caller's own data, for LGPD rights)
- **The root group requires authorization.** Anonymous endpoints opt out explicitly with
  `.AllowAnonymous()`, so access is denied by default.
- Endpoints talk to Application use cases and return `Contracts` types. They never return domain types.

## Checklist for every endpoint

1. Typed results (`Results<Accepted<T>, ValidationProblem, ProblemHttpResult>`) so OpenAPI is accurate.
2. Explicit `.RequireAuthorization(Policies.X)` or `.AllowAnonymous()` together with a rate-limit policy.
3. Input validated at the boundary: .NET 10 built-in validation (`AddValidation()`) on contract
   records, plus domain value objects (`Floor`) for the business rules.
4. A `RequireRateLimiting(...)` policy.
5. Every POST that creates work honours an `Idempotency-Key` header; replays return the original response.
6. Errors are ProblemDetails (RFC 9457) carrying `traceId`. Never return exception messages or stack traces.
7. OpenAPI metadata (`WithSummary`, `WithTags`, `Produces...`); the OpenAPI snapshot test is updated.
8. A row in the authorization-matrix test (endpoint × anonymous/Operator/Admin/Auditor).
9. A `CancellationToken` parameter, passed all the way down.

## Status codes

| Situation | Status |
|---|---|
| Request accepted and queued | `202 Accepted` + request ID (+ `Location`) |
| Invalid input (floor 0, floor 11, unknown direction) | `400` validation problem |
| Not authenticated / not allowed | `401` / `403` |
| Elevator out of service, invalid transition | `409` with a machine-readable `reason` |
| Rate limit hit | `429` + `Retry-After` |
| Engine or broker unavailable | `503` + `Retry-After` (never claim "accepted" without a publisher confirm) |

## Authentication and authorization

- ASP.NET Core Identity with cookie authentication: `HttpOnly`, `Secure`, `SameSite=Strict`,
  20-minute sliding expiry. The browser never holds bearer tokens.
- Antiforgery validation on every state-changing request made with a cookie.
- Admins must use TOTP two-factor authentication; lockout after 5 failed attempts.
- Roles: `Admin`, `Operator`, `Auditor`. Policies: `CanOperate`, `CanAdminister`, `CanAudit`.
  Check policies, never role strings, in endpoints.

## Abuse protection and headers

- Rate limiting:
  - partitioned per client IP for anonymous calls, per user for authenticated ones
  - plus a global concurrency limiter in front of the request intake
  - the IP is used only in memory for partitioning and is never stored
- Security headers middleware:
  - `Content-Security-Policy` with `default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; frame-ancestors 'none'`
  - `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, a restrictive `Permissions-Policy`
  - HSTS in production
- The server header is off. `AllowedHosts` is set per environment (not `*` in production).
- OpenAPI document and API reference UI are mapped in Development only.

## SignalR hub (`/hubs/elevator`)

- Anonymous clients may only **receive** snapshots. Commands always go through REST, so
  validation, authorization and rate limits are enforced in exactly one place.
- Admin-only hub groups require the `CanOperate` policy.

## Logging

Don't log request bodies, headers, cookies or IP addresses. The audit log records staff actions
with a pseudonymised IP (see the `security-lgpd` skill). Use `LoggerMessage` methods, event IDs 3000–3999.
