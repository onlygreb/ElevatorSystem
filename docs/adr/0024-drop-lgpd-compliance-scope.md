# 0024. Keep privacy by design, without an LGPD compliance programme

- **Status:** Accepted
- **Date:** 2026-10-20
- **Supersedes:** [0009](0009-privacy-by-design-for-lgpd.md)

## Context

ADR-0009 made LGPD compliance part of the scope: a processing record, an impact report, a retention
schedule, a privacy notice, an incident-response runbook and data-subject features (export, deletion
requests). Writing and maintaining those documents took a large share of each phase, while the product
goal is the elevator itself: passengers, the control engine, the admin view and their quality. The
technical safeguards ADR-0009 introduced cost little and protect people regardless of any law.

## Decision

1. **The LGPD compliance programme is out of scope.** The documents under `docs/lgpd/` are removed and
   no data-subject features (self-service export, deletion requests, privacy notice page) are planned.
2. **The privacy-protecting design stays**, as engineering rules rather than legal obligations:
   - passengers are anonymous; a passenger is a random ID the browser keeps, never linked to a person,
     device, address or card
   - staff e-mails, passwords, keys and client addresses never reach logs, traces or metrics, and
     `LogPrivacyTests` enforces it
   - client addresses in the audit trail are keyed HMAC pseudonyms
   - the audit chain keeps the salted personal digests of ADR-0014, so a staff member's entries can
     still be anonymised without breaking verification (`AuditTrail.AnonymiseActorAsync`)
3. The `security` skill replaces the `security-lgpd` skill.

## Alternatives considered

- **Keep the full programme:** the right call for a deployment that processes personal data in Brazil,
  and the data structures still support it; it is too costly for this project's goal.
- **Remove the safeguards as well:** saves little and makes the system worse for everyone.

## Consequences

- A real deployment in Brazil would need the compliance documents written for its own context; the
  design does not prevent it.
- References to ADR-0009 in code now point here.
