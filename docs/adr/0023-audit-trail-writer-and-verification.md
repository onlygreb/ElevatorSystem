# 0023. The API writes the audit trail, before acting, as one hash chain

- **Status:** Accepted
- **Date:** 2026-10-20

## Context

ADR-0009 requires a tamper-evident audit trail and ADR-0014 fixes how an entry is hashed so it can be
anonymised later. The threat model left open who writes the chain and how two writers avoid forking it
(T-AUD-01, OQ-4), how a safety command is traced to the staff member behind it when the message
envelope has no actor (T-ENG-04), and what happens when the trail cannot be written.

## Decision

1. **One writer: the API.** Every audited action is a staff action that enters through the API: sign-ins
   (succeeded, failed, locked out), sign-outs, two-factor registration and service actions. The engine
   does not write the trail; what the car did is in its own activity feed and logs. This answers OQ-4 for
   phase 4.
2. **Appends are serialised twice.** In the process, `AuditTrail` takes a lock, reads the newest entry and
   links the new one to it. In the database the sequence is the primary key, so a second writer (another
   API instance) gets a conflict instead of a fork; the trail re-reads the head and retries, up to three
   times.
3. **Audit first, act second (fail closed).** A service action is recorded as `requested`, with its
   command ID in the target, *before* the command is published; if the broker then refuses it, a second
   entry records `refused`. If the entry cannot be written, the request fails and nothing is sent. A
   successful sign-in that cannot be audited is answered with an error and its cookie is discarded. The
   command ID links the audit entry to the engine's logs and activity, which answers T-ENG-04 without
   putting staff identities on the broker.
4. **What an entry holds** follows ADR-0014: sequence, time (UTC, whole milliseconds, so every store
   returns it exactly), action, target, outcome; the staff member's ID and the client's pseudonym only
   through a per-entry salted digest. Targets never contain personal data: a failed sign-in records
   neither the e-mail nor anything typed.
5. **Client pseudonyms:** `keyId:` followed by 16 bytes of `HMAC-SHA256(key, address)` in hex. The key is a
   secret (`Audit:ClientPseudonymKey`, a generated Aspire parameter locally); without one, a random key is
   generated at every start and a warning is logged.
6. **Verification** (`GET /api/v1/admin/audit/verification`) walks the whole chain from the first entry and
   reports the first position that fails: a gap (an entry removed or moved), a personal digest that no
   longer matches (personal fields changed), or a hash that no longer matches (anything else). It also
   returns the head's sequence and hash, to note down elsewhere: removing entries from the end can only be
   detected against a head recorded outside the database (the anchoring part of OQ-14, still open).
7. **Anonymisation** (`AuditTrail.AnonymiseActorAsync`) erases the actor, pseudonym and salt of a staff
   member's entries, marks them with the request reference, and appends an entry saying how many were
   anonymised. The chain still verifies. The deletion-request workflow that calls it is phase 5.

## Alternatives considered

- **The engine (or a dedicated consumer) writes the trail from broker messages:** needs an actor on every
  command and a second writer for sign-ins, which is exactly the fork T-AUD-01 describes.
- **Record after acting:** a crash between the two leaves an action with no trace, the worst case for an
  audit trail.
- **A database sequence or `SERIAL` for the entry number:** sequences skip numbers on rollback, which
  verification could not tell from a deleted entry.

## Consequences

- ADR-0014 is implemented and accepted, except checkpoints, which arrive with the retention job.
- A database outage makes staff sign-in and service actions fail rather than go unrecorded. The elevator
  itself keeps working (ADR-0007), and passengers are not affected.
- Running more than one API instance stays safe for the chain, but idempotency keys (ADR-0022) would then
  need shared storage.
- The trail is append-only by code, not yet by database grants (OQ-7, T-AUD-06).
