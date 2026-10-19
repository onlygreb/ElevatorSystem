---
name: security-lgpd
description: Security and LGPD (Brazilian General Data Protection Law) rules for ElevatorSystem - threat model summary, the per-change security checklist, data classification table, legal bases, retention, data subject rights, pseudonymisation, log redaction, incident response, and where the security and privacy documents live. Use for any change that touches authentication, authorization, personal data, logging, audit, configuration secrets, dependencies, or before declaring a feature done.
---

# Security and LGPD

Decisions behind this skill: ADR-0008 (supply chain) and ADR-0009 (privacy by design).
Detailed documents: `docs/security/` (threat model) and `docs/lgpd/` (processing record, impact
report, retention, incident response). These are written as each phase introduces the data.

## Main threats and their controls

| Threat | Control |
|---|---|
| Request flooding ("press every button 1,000 times") | Rate limits, bounded intake channel, request merging |
| Admin account takeover | Identity with TOTP 2FA, lockout, password policy, short sliding sessions |
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
- [ ] No personal data in logs, traces, metrics or exception messages
- [ ] Error responses reveal nothing internal
- [ ] Cryptography only through the platform: ASP.NET Core Data Protection, `HMACSHA256`, `RandomNumberGenerator`. Never hand-rolled
- [ ] Database access only through EF Core (parameterised); no string-built SQL
- [ ] Any new package: licence checked against ADR-0008, audit passes, lock files committed
- [ ] Negative-path tests exist (invalid input, missing permission, replayed request)

## Data classification

| Data | Category | Legal basis (LGPD Art. 7) | Default retention | Handling |
|---|---|---|---|---|
| Hall and car requests (floor, direction, timestamps) | Not personal | n/a | 90 days, then aggregated | Never linked to a user, device, IP or card |
| Staff account (name, e-mail, role, password hash, 2FA secret) | Personal | Contract performance / legitimate interest (V, IX) | While active + 6 months | Identity hashing; 2FA secret encrypted with Data Protection |
| Audit trail (staff ID, action, timestamp, pseudonymised IP) | Personal, pseudonymised | Legitimate interest + security duty (IX, Art. 46) | 1 year | IP stored only as HMAC-SHA256 with a rotating key |
| Technical logs and traces | Must contain no personal data | n/a | 30 days | Redaction enforced by tests |

Retention periods are defaults in configuration and must be confirmed by the data controller.
**Adding any personal field means updating this table, the processing record (ROPA) and the
impact report (RIPD) in the same change.**

## LGPD obligations and how the system meets them

- **Necessity and data minimisation (Art. 6 III):** passengers are anonymous by design. Nothing
  identifies who called the elevator.
- **Data subject rights (Art. 18):** staff can export their data as JSON (access and portability)
  and request deletion. Deletion anonymises audit entries with a tombstone instead of deleting
  them, which keeps the hash chain verifiable.
- **Retention and end of processing (Art. 15–16):** a scheduled job anonymises or purges data
  past its retention period and records that it ran.
- **Security (Art. 46):**
  - TLS everywhere
  - encryption of secrets at rest
  - least-privilege roles
  - audit of every privileged action
- **Transparency (Art. 9):** a privacy notice page in the UI names the controller and the DPO
  contact (both configurable) and the purposes of processing. Only strictly necessary cookies are
  used, so no consent banner is needed, but the notice says so.
- **Incident response (Art. 48, ANPD Resolution CD/ANPD nº 15/2024):** the runbook in
  `docs/lgpd/incident-response.md` covers notifying ANPD and data subjects within 3 business days
  of becoming aware of an incident that may cause relevant risk or damage.

## Redaction in practice

- Classify fields with `Microsoft.Extensions.Compliance` attributes (`[PersonalData]`,
  `[SensitiveData]` from our taxonomy) and enable redaction with `AddRedaction()`.
- Log through `LoggerMessage` methods with `[LogProperties]`, so classified properties are
  redacted automatically.
- A test captures logs with `FakeLogger` and fails if any known personal value appears unredacted.
