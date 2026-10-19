# 0009. Privacy by design for LGPD

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

The system will be operated in Brazil and must comply with the LGPD (Lei nº 13.709/2018). An
elevator needs almost no personal data to work, but the surrounding system (staff accounts, audit
trail, logs, IP addresses) easily accumulates it by accident. Under the LGPD an IP address is
personal data, and every personal data item needs a purpose, a legal basis and a retention period.

## Decision

- **Passengers are anonymous by design (Art. 6 III, necessity).** Requests carry a floor, a
  direction and timestamps, never a user, card, device or IP address. That rules out the most
  sensitive data category entirely instead of protecting it.
- **Personal data is limited to staff:**
  - accounts: name, e-mail, role, credentials
  - the audit trail: staff ID, action, timestamp, IP address stored only as HMAC-SHA256 with a rotating key
  - each item has a documented legal basis and retention period (see the `security-lgpd` skill)
- **Logs and traces contain no personal data.** Fields are classified with
  `Microsoft.Extensions.Compliance` attributes and redacted automatically, and a test fails if a
  known personal value reaches a log.
- **Retention is enforced by a scheduled job** that anonymises or purges data past its period (Art. 15–16).
- **Data subject rights (Art. 18):** self-service export (JSON) and deletion. Deletion anonymises
  audit entries with a tombstone instead of removing them, so the tamper-evident hash chain stays verifiable.
- **Transparency and accountability:**
  - a privacy notice in the UI
  - a configurable DPO (encarregado) contact
  - a record of processing operations (ROPA, Art. 37)
  - a data protection impact report (RIPD, Art. 38)
  - an incident-response runbook (Art. 48 and ANPD Resolution nº 15/2024: notification within
    3 business days)
  - all of these live in `docs/lgpd/`

## Alternatives considered

- **Identifying passengers (badges, accounts) for analytics:** brings in personal and possibly
  sensitive location data with no operational need, which fails the necessity test.
- **Storing raw IPs for security forensics:** a keyed HMAC still lets us correlate abuse from the
  same source without keeping the address itself.

## Consequences

- Most of the system handles no personal data at all, which shrinks the compliance surface and
  the impact of any breach.
- Any new personal field requires updating the classification, the ROPA and the RIPD in the same change.
- Only strictly necessary cookies are used (authentication, antiforgery, culture preference), so
  no consent banner is required. The privacy notice explains this.
