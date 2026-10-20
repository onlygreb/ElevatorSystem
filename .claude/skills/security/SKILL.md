---
name: security
description: Security rules for ElevatorSystem - threat model summary, the per-change security checklist, data classification, pseudonymisation, log redaction, staff authentication and the audit trail, and where the security documents live. Use for any change that touches authentication, authorization, staff data, logging, audit, configuration secrets, dependencies, or before declaring a feature done.
---

# Security

Decisions behind this skill: ADR-0008 (supply chain), ADR-0014 (salted digests in the audit chain),
ADR-0022 (staff authentication) and ADR-0023 (audit trail). The threat model is in
`docs/security/threat-model.md`; how to report a vulnerability is in `SECURITY.md`.

## Main threats and their controls

| Threat | Control |
|---|---|
| Request flooding ("press every button 1,000 times") | Rate limits, bounded intake channel, request merging |
| Staff account takeover | Identity with a second factor mandatory for every role, lockout, password policy, per-address sign-in limit, 20-minute sliding `__Host-` cookie (ADR-0022) |
| CSRF against cookie auth | `SameSite=Strict` cookies + antiforgery tokens |
| Tampering with the audit trail | Hash-chained, append-only audit entries with a verification endpoint |
| Forged messages on the broker | Per-service credentials, a dedicated virtual host, validation of every message |
| Information leaking via errors and logs | ProblemDetails without internals, log redaction, no request bodies in logs |
| Vulnerable or hostile dependencies | NuGet audit fails the build, lock files, source mapping, SHA-pinned Actions, CodeQL, Dependabot |
| Unsafe client code | Strict CSP, no secrets in the WebAssembly bundle, server-side enforcement of everything |

## Checklist for every change

- [ ] Input is validated at the boundary, and output is encoded by the framework (no raw `MarkupString` from data)
- [ ] Authorization is explicit (policy or `AllowAnonymous` plus rate limit) and covered by the matrix test
- [ ] No secrets in code, `appsettings*.json` or tests. Use user-secrets, Aspire parameters or environment secrets
- [ ] No staff e-mails, passwords, keys or client addresses in logs, traces, metrics or exception messages
- [ ] Error responses reveal nothing internal
- [ ] Cryptography only through the platform: ASP.NET Core Data Protection, `HMACSHA256`, `RandomNumberGenerator`. Never hand-rolled
- [ ] Database access only through EF Core (parameterised); no string-built SQL
- [ ] Any new package: licence checked against ADR-0008, audit passes, lock files committed
- [ ] Negative-path tests exist (invalid input, missing permission, replayed request)

## Data classification

| Data | Category | Handling |
|---|---|---|
| Hall and car requests, passenger positions | Not personal: passengers are anonymous | Never linked to a person, device, IP or card; a passenger is a random ID the browser keeps |
| Staff account (e-mail, role, password hash, 2FA secret, recovery codes) | Sensitive | Identity hashing; 2FA secret and recovery codes encrypted with Data Protection (`ProtectedStaffUserStore`) |
| Audit trail (staff ID, action, timestamp, pseudonymised IP) | Sensitive, pseudonymised | IP stored only as a keyed HMAC-SHA256 pseudonym; salted personal digests so a staff member's entries can be anonymised without breaking the chain (ADR-0014, ADR-0023) |
| Client IP for anonymous rate limiting | Sensitive | Memory only, for the rate-limit window; never written to the database, logs, traces or metrics |
| Technical logs and traces | Must hold no sensitive data | Redaction enforced by `LogPrivacyTests` |
| HMAC key that pseudonymises IPs | Secret | A secret parameter (`Audit:ClientPseudonymKey`), never in the database; random per start when missing |

## Redaction in practice

- Log through `LoggerMessage` methods; never log request bodies, headers, cookies or addresses.
- `LogPrivacyTests` captures every category at `Trace` and fails if a client address, a request body,
  a staff e-mail, a password or an authenticator key appears.
