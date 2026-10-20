# Record of processing operations (Registro das Operações de Tratamento)

> [!IMPORTANT]
> System documentation draft for the data controller (controlador) and their legal counsel to
> validate. Not legal advice. Placeholders look like `{{LIKE_THIS}}`; retention periods are
> configurable defaults the controller must confirm.

LGPD Art. 37 requires the controller and the processor (operador) to keep a record of the
personal data processing operations they carry out. This record covers the processing performed
**by ElevatorSystem itself**. It follows the data classification table in the `security-lgpd`
skill and the decisions in [ADR-0009](../adr/0009-privacy-by-design-for-lgpd.md).

## Parties

| Role | Who | Contact |
|---|---|---|
| Controller (controlador) | `{{CONTROLLER_LEGAL_NAME}}`, CNPJ `{{CONTROLLER_CNPJ}}`: the organisation that operates the building and deploys ElevatorSystem | `{{CONTROLLER_ADDRESS}}` |
| Data protection officer (encarregado) | `{{DPO_NAME}}` | `{{DPO_EMAIL}}`, `{{DPO_PHONE}}` |
| Processor (operador): hosting | `{{HOSTING_PROVIDER}}`, region `{{HOSTING_REGION}}` | `{{HOSTING_PROVIDER_CONTACT}}` |
| Processor (operador): operation and support of the system, if outsourced | `{{SUPPORT_PROVIDER}}` | `{{SUPPORT_PROVIDER_CONTACT}}` |

The engineering team that writes ElevatorSystem is neither controller nor processor merely by
writing the software. It becomes a processor only if it hosts or operates a deployment for the
controller, in which case it fills one of the processor rows above.

## Summary

| # | Operation | Personal data? | Legal basis (Art. 7) | Default retention | Phase |
|---|---|---|---|---|---|
| 1 | [Staff accounts and authentication](#1-staff-accounts-and-authentication) | Yes | V (contract) or IX (legitimate interest) | While active + 6 months | 4 |
| 2 | [Audit trail](#2-audit-trail) | Yes, pseudonymised | IX (legitimate interest), serving the Art. 46 security duty | 1 year | 4 |
| 3 | [Technical logs, traces and metrics](#3-technical-logs-traces-and-metrics) | No, by design and by test | n/a | 30 days | 2 onwards |
| 4 | [Passenger requests](#4-passenger-requests-not-personal-data) | No | n/a | 90 days, then aggregated | 1 onwards |
| 5 | [Transient use of client IP addresses for rate limiting](#5-transient-use-of-client-ip-addresses-for-rate-limiting) | Yes, in memory only | IX (legitimate interest) | Not stored | 2 onwards |

## 1. Staff accounts and authentication

| Field | Entry |
|---|---|
| Purpose | Let authorised staff (administrators and auditors, ADR-0022) sign in to the admin view: administrators take the elevator out of service and return it; both see occupancy and the audit trail. Permissions are limited to the role. |
| Legal basis (Art. 7) | **V, performance of a contract** to which the data subject is party, for the controller's own employees (the employment or service contract requires them to operate the system). **IX, legitimate interest** of the controller, for staff of third parties such as maintenance contractors, whose contract is with their employer rather than with the controller. A legitimate interest assessment (Art. 10) must be kept for this case. |
| Data categories | Work e-mail address (also the user name); role (`Admin` or `Auditor`); password hash (salted and iterated, ASP.NET Core Identity); TOTP two-factor secret and one-time recovery codes (both encrypted with ASP.NET Core Data Protection before they are stored); security stamp; failed sign-in counter and lockout end time. No name is collected (data minimisation, Art. 6 III). No sensitive personal data (Art. 5 II), no data about children or adolescents. |
| Data subjects | Staff of the controller and of its contractors who are given access to the operations panel. |
| Source | Created at deployment from the `Staff:Accounts` configuration (there is no public registration); the initial password comes from the deployment's secret store and the two-factor secret is registered by the staff member. An account management screen is planned. |
| Retention | While the account is active, plus **6 months** after deactivation, then deleted (see [retention schedule](retention.md)). |
| Recipients | The staff member (their own session). Administrators and auditors see staff e-mails next to audit entries. Nobody else, inside or outside the system. |
| Processors | Hosting provider `{{HOSTING_PROVIDER}}` (database storage). E-mail delivery provider `{{EMAIL_PROVIDER}}`, only if account e-mails such as password reset are enabled. |
| International transfer | None by design. If `{{HOSTING_REGION}}` or `{{EMAIL_PROVIDER}}` is outside Brazil, the transfer needs one of the mechanisms of Art. 33, such as the standard contractual clauses of Resolution CD/ANPD nº 19/2024, and this entry must name it. |
| Security measures | TLS for every connection; `HttpOnly`, `Secure`, `SameSite=Strict` session cookie with a 20-minute sliding expiry and antiforgery tokens; a second factor (TOTP or a one-time recovery code) mandatory for every staff role; lockout for 15 minutes after 5 failed attempts and a per-address sign-in rate limit; the Data Protection key ring in the staff database (encrypting it with a key from outside the database is still open, threat model OQ-13); least-privilege roles and policies; sign-ins, sign-outs and two-factor registration recorded in the audit trail. |
| Data subject rights | Self-service export (JSON) and deletion request from the operations panel, plus requests to the encarregado (see the [privacy notice](privacy-notice.md#your-rights)). |

## 2. Audit trail

| Field | Entry |
|---|---|
| Purpose | Accountability and security: know which staff member performed each privileged action (emergency stop, maintenance, return to service, and from phase 5 data export and deletion) and every sign-in attempt, detect misuse, and support investigation of incidents. The trail is append-only and hash-chained so tampering is detectable. |
| Legal basis (Art. 7) | **IX, legitimate interest** of the controller in the security and accountability of a safety-related system, which also serves the duty to adopt security measures under Art. 46. A legitimate interest assessment (Art. 10) must be kept. Counsel should confirm whether **VI** (regular exercise of rights in judicial, administrative or arbitration proceedings) applies to entries placed under a legal hold. |
| Data categories | Staff account ID (an opaque identifier, linkable to the account while it exists; absent for a sign-in with an unknown e-mail); action and its target (never the e-mail or anything typed); outcome; timestamp; client IP address **stored only as a keyed pseudonym** (the first 128 bits of HMAC-SHA256), never in clear; a random per-entry salt, the salted digest of the personal fields and the entry hash (ADR-0014, ADR-0023). |
| Data subjects | Staff who use the operations panel. |
| Source | Generated by the API when someone signs in or out, registers a second factor or requests a service action, before the action is carried out. |
| Retention | **1 year**, then purged (see [retention schedule](retention.md)). When a staff member exercises the right to deletion, their entries are anonymised with a tombstone instead of removed, so the hash chain stays verifiable. |
| Recipients | Staff with the `Auditor` or `Admin` role signed in with their second factor (`staff` policy). The ANPD, courts or other authorities on a lawful request. |
| Processors | Hosting provider `{{HOSTING_PROVIDER}}` (database storage). |
| International transfer | As for operation 1. |
| Security measures | Append-only storage with a hash chain and a verification endpoint; IP pseudonymisation with a keyed HMAC whose key rotates and is kept as a secret, never stored alongside the pseudonyms; read access only through the `staff` policy; TLS; encryption at rest provided by the hosting environment. |
| Data subject rights | Access and export include the subject's own audit entries. Deletion anonymises them (see [retention schedule, tombstones](retention.md#4-audit-tombstones-and-the-hash-chain)). |

## 3. Technical logs, traces and metrics

| Field | Entry |
|---|---|
| Purpose | Operate, monitor and troubleshoot the system (OpenTelemetry logs, traces and metrics). |
| Personal data | **None, by design.** Fields are classified with `Microsoft.Extensions.Compliance` attributes and redacted automatically; request bodies, headers, cookies and IP addresses are never logged; a test captures logs and fails if a known personal value appears. This entry is in the record because the risk of accidental inclusion is real and the controls must be documented. |
| Legal basis (Art. 7) | n/a while the data contains no personal data. If personal data is ever found in logs, it is treated as an incident (see the [runbook](incident-response.md)) and removed. |
| Data subjects | n/a |
| Retention | **30 days**, enforced by the telemetry backend's retention setting. |
| Recipients | Engineers and operators with access to the telemetry backend: the Aspire dashboard locally, `{{TELEMETRY_BACKEND}}` in production. |
| International transfer | n/a while no personal data is present. The location of `{{TELEMETRY_BACKEND}}` must still be recorded. |
| Security measures | Automatic redaction, log-capture tests, structured logging through `LoggerMessage` methods only, access to the telemetry backend restricted to operations staff. |

## 4. Passenger requests (not personal data)

| Field | Entry |
|---|---|
| Purpose | Call the elevator and serve requests (hall calls and car calls); keep recent history for operation and aggregate statistics. |
| Personal data | **None.** A request holds a floor, a direction and timestamps. It is never linked to a user, card, badge, device, IP address or camera. Anonymity is a design decision (ADR-0009, Art. 6 III necessity), so this operation is outside the LGPD's scope, as it does not concern an identified or identifiable natural person (Art. 5 I). |
| Residual consideration | In a small building, a floor and a time could in theory single out a person when combined with outside knowledge (for example, the only tenant of a floor). The system holds no such outside knowledge and never joins requests to other data; the [RIPD](ripd.md) assesses this as risk R6. |
| Legal basis (Art. 7) | n/a |
| Retention | **90 days**, then aggregated into counts per floor and hour, and the individual requests deleted. |
| Recipients | Anyone viewing the passenger screen (live state only); staff in the operations panel (history and statistics). |
| Where it is held | Since phase 3 (ADR-0021) a call travels as a command through RabbitMQ (`elevator.commands.engine`, until the engine has applied it) and as an update to every API instance; the engine keeps pending calls in its snapshot and the command IDs it applied for 7 days in PostgreSQL (ADR-0019). None of these holds more than the floor, direction, timestamps and random IDs. Occupancy counts derived from the calls are kept in memory and published as totals only (ADR-0017). |
| Security measures | Rate limits, request merging and a bounded intake channel protect availability; no identifier is ever attached to a request. |

## 5. Transient use of client IP addresses for rate limiting

| Field | Entry |
|---|---|
| Purpose | Protect the service from request flooding by partitioning rate limits per client IP for anonymous calls. |
| Legal basis (Art. 7) | **IX, legitimate interest** in the availability and security of the service, serving the Art. 46 security duty. |
| Data categories | Client IP address, held **in memory only** for the life of the rate-limit window, as the key of a token bucket. IPv6 addresses are first reduced to their /64 network prefix. It is never written to the database, logs, traces or metrics. |
| Data subjects | Anyone who uses the passenger screen or the API, including staff. |
| Retention | Not stored. It disappears when the rate-limit window expires or the process restarts. |
| Recipients | None. |
| Security measures | In-memory only; logging of IP addresses is forbidden and covered by the log-capture test (`LogPrivacyTests`, which captures every log category at `Trace`). |

## Out of scope of this record

This record covers only ElevatorSystem. The controller must record separately any processing done
around it, for example:

- access logs of a reverse proxy, load balancer or web application firewall placed in front of
  the API by the deployment environment (these usually contain IP addresses)
- the hosting provider's own platform logs
- building access control, visitor registration and CCTV, which are separate systems

## Revision history

| Date | Change | Reviewed by |
|---|---|---|
| 2026-10-19 | First draft, written ahead of phases 4 and 5 | Engineering; pending controller and counsel review |
| 2026-10-19 | Legal basis wording of operations 2 and 5 aligned with the classification table | Engineering; pending controller and counsel review |
| 2026-10-19 | Operation 4: where calls are held now that the engine runs behind RabbitMQ and PostgreSQL (ADR-0021) | Engineering; pending controller and counsel review |
