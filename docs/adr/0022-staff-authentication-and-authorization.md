# 0022. Authenticate staff with Identity cookies and a mandatory second factor

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

Phase 4 adds the people who run the building: admins, who take the car out of service and return it
(ADR-0017), and auditors, who check what was done. Passengers stay anonymous (ADR-0009). ADR-0006
already chose same-origin `HttpOnly` cookies over tokens in the browser. The threat model left open
how strong staff sign-in has to be (T-API-01, OQ-12), how quickly a revoked session stops working
(T-API-02, OQ-11), how retries of admin actions are handled (T-API-05, OQ-15), where the Data
Protection key ring lives (T-DB-05, OQ-13) and who may see the occupancy counts, which until now
anyone could read.

## Decision

1. **ASP.NET Core Identity** with its own endpoints under `/api/v1/auth`: sign-in, sign-out, the
   current session, two-factor setup and enable. There is **no public registration**: accounts are
   created at start-up from the `Staff:Accounts` configuration, only when missing, with passwords from
   a secret store (Aspire parameters locally). Identity's API endpoints (`MapIdentityApi`) are not used
   because they include registration, bearer tokens and password reset by e-mail.
2. **Two roles**, `Admin` and `Auditor`. Both see occupancy and the audit trail; only admins request
   service actions. Admins never place calls (ADR-0017).
3. **Every staff capability requires a sign-in with the second factor** (TOTP, RFC 6238, or a one-time
   recovery code). Identity marks such a session with the `amr: mfa` claim and the policies require it.
   A session signed in with the password alone can see itself and register an authenticator app,
   nothing else. This answers OQ-12 for every role, not only admins.
4. **Session cookie** `__Host-elevator-session`: `Secure`, `HttpOnly`, `SameSite=Strict`, path `/`, no
   domain, no persistent expiry, 20 minutes sliding. The security stamp is re-checked every minute, so a
   password change, a new authenticator or a disabled account ends other sessions within a minute; the
   check keeps the `amr` claim. A hub connection is closed when its cookie expires (OQ-11, in part).
5. **Antiforgery on every staff `POST`**, sign-in included: the token comes from
   `GET /api/v1/auth/antiforgery` and travels in `X-CSRF-TOKEN`, checked by an endpoint filter that
   refuses with a `400` problem (reason `Antiforgery`). The token is bound to the user, so the browser
   fetches a new one after signing in or out.
6. **Brute force:** lockout for 15 minutes after 5 failures (wrong two-factor codes count), a per-address
   rate limit on sign-in (10, then one every 6 s), and a per-account limit on staff endpoints. Unknown
   e-mails, wrong passwords and locked accounts get the same answer, and an unknown e-mail still costs one
   password hash, so neither the answer nor its timing reveals which accounts exist.
7. **Passwords:** at least 12 characters and 4 distinct ones, no composition rules (NIST SP 800-63B),
   hashed by Identity (PBKDF2).
8. **Secrets at rest:** Identity's own tokens (the authenticator key and the recovery codes) are encrypted
   with ASP.NET Core Data Protection before they are stored (`ProtectedStaffUserStore`). The key ring is
   kept in the staff database (`data_protection_key`), so restarts and a second API instance can read it
   (OQ-13). The key ring itself is not encrypted at rest yet; production should add a key-encryption key
   (`ProtectKeysWith…`), which stays open.
9. **Storage:** accounts, roles, the audit trail (ADR-0023) and the key ring are in the `staff` schema of
   the PostgreSQL database, migrated by the API. In single-process mode (ADR-0021) the same model lives in
   an in-memory SQLite database that disappears with the process.
10. **Occupancy is staff-only:** `GET /api/v1/building/occupancy` requires the staff policy, and the hub
    sends `OccupancyChanged` only to connections that met it when they connected. Status and activity
    stay public: passengers need them to use the elevator; per-floor counts tell an outsider which floors
    are empty.
11. **Idempotent service actions:** an `Idempotency-Key` (a UUID) on `POST /api/v1/admin/service-actions`
    returns the first acceptance to a retry instead of acting twice; keys are kept in memory per account
    for 10 minutes, and refusals are not remembered so they can be retried (OQ-15).

## Alternatives considered

- **Bearer tokens (JWT) in the browser:** ADR-0006 already rejected them; a script can read them.
- **An external identity provider (OIDC):** the right choice inside an organisation that has one, and
  the cookie design does not prevent adding it later. It would make the system depend on a service it
  cannot ship with.
- **Second factor for admins only:** auditors read the audit trail, which holds personal data; one rule
  for all staff is simpler to explain and to test.
- **WebAuthn passkeys:** stronger against phishing, but needs a library outside the platform today; TOTP
  is built into Identity. Worth revisiting.
- **Keeping occupancy public:** simpler, but per-floor presence is information about the building that
  passengers do not need.

## Consequences

- The API now needs the database, so Aspire wires it to PostgreSQL and waits for it. In distributed mode
  the API prepares the staff schema in the background and reports not ready (`staff-database` check)
  until it is done.
- The first admin's password is a generated Aspire parameter; the admin reads it from the dashboard, signs
  in and must register an authenticator app before doing anything else.
- Account management (inviting staff, disabling an account, resetting a second factor) is configuration
  only for now. It is the next staff feature to build.
- Behind a TLS-terminating proxy the `Secure` cookies and the antiforgery checks need forwarded headers
  from known proxies, still open as OQ-10.
- `AuthorizationMatrixTests` pins who may call each endpoint, and a test fails if any `/api` endpoint
  declares neither `AllowAnonymous` nor a policy.
