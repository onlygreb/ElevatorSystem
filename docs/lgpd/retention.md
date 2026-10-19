# Retention schedule (tabela de temporalidade)

> [!IMPORTANT]
> System documentation draft for the data controller (controlador) and their legal counsel to
> validate. Not legal advice. Every period below is a **configurable default** that the
> controller must confirm. Placeholders look like `{{LIKE_THIS}}`. The retention job and the
> tombstone mechanism are planned for phases 4 and 5 and do not exist yet; this document is the
> specification they will be built and tested against.

LGPD Art. 15 says processing ends when its purpose is achieved or its period expires, and Art. 16
says personal data must then be deleted, except where the law allows it to be kept (for example
to comply with a legal obligation, or in anonymised form). ADR-0009 commits the system to a
scheduled job that enforces this.

## 1. Schedule

The first four rows match the classification table in the `security-lgpd` skill.

| Data | Personal? | Retention (default) | Counted from | At expiry |
|---|---|---|---|---|
| Hall and car requests (floor, direction, timestamps) | No | 90 days | Request completed or rejected | **Aggregate, then purge**: counts per floor, direction and hour are kept; individual requests are deleted |
| Staff account (name, e-mail, role, credentials, 2FA data) | Yes | While active + 6 months | Account deactivated | **Purge** the account and its credentials |
| Audit trail (staff ID, action, timestamp, pseudonymised IP) | Yes, pseudonymised | 1 year | Entry written | **Purge** the oldest entries and record a chain checkpoint ([section 4](#4-audit-tombstones-and-the-hash-chain)) |
| Technical logs and traces | No, by design | 30 days | Record emitted | **Purge**, by the telemetry backend's own retention setting |
| Aggregated request statistics | No | Indefinite | | Kept; they cannot identify anyone |
| Client IP for rate limiting | Yes | Not stored (memory only, for the rate-limit window) | | Discarded automatically |
| HMAC keys used to pseudonymise IPs | Enables linking | Rotated every `{{HMAC_KEY_ROTATION_PERIOD}}`; retired keys destroyed after `{{HMAC_KEY_RETENTION}}` | Key retired | **Destroy**: pseudonyms made with that key can no longer be linked to each other or recomputed from an IP |
| Database backups | Contain all of the above | `{{BACKUP_RETENTION}}` (recommended: 35 days or less) | Backup taken | **Purge** by the backup system's rotation; see [backups](#backups) |
| Records of data subject requests (who asked, what, when, outcome) | Yes, pseudonymised | 1 year, as audit entries | Request closed | As the audit trail |
| Incident records | May contain personal data | At least 5 years (Resolution CD/ANPD nº 15/2024) | Incident recorded | Reviewed by the encarregado, then purged ([incident runbook](incident-response.md#8-record-keeping)) |

Proposed configuration keys (final names are set when the job is implemented):

```json
{
  "Retention": {
    "PassengerRequests": "90.00:00:00",
    "DeactivatedStaffAccounts": "180.00:00:00",
    "AuditEntries": "365.00:00:00",
    "RunAt": "03:00:00",
    "TimeZone": "America/Sao_Paulo"
  }
}
```

## 2. What happens at expiry: anonymise or purge

- **Purge** means the row is deleted from the database. It disappears from backups when the
  backup that holds it rotates out.
- **Anonymise** means the data is changed so that it can no longer be linked to a person by
  reasonable means available at the time (Art. 5 III and Art. 12). Anonymised data is no longer
  personal data. The system anonymises in two cases only:
  - **aggregation** of passenger requests, which were never personal, so that history beyond
    90 days is kept only as counts
  - **audit tombstones**, used when a staff member exercises the right to deletion before their
    audit entries expire ([section 4](#4-audit-tombstones-and-the-hash-chain))
- Everything else is purged. Purging is preferred because it leaves nothing to re-identify.

Purging a staff account does not change their audit entries: those keep the opaque staff ID
until they expire on their own schedule, because the accountability purpose outlives the
account by up to a year. A deletion request (Art. 18 VI) is what triggers tombstones early.

### Backups

Backups are copies, not a separate retention period. To keep deletions effective:

- backups are encrypted and kept for no longer than `{{BACKUP_RETENTION}}`
- the system keeps a **deletion ledger**: the opaque IDs of purged accounts and tombstoned audit
  entries, with no other personal data
- after any restore, before the system reopens, the retention job runs and the deletion ledger is
  re-applied, so nothing deleted comes back

## 3. The retention job

**Where it runs.** A background service in the Infrastructure layer, active in exactly one
instance at a time (for example under the leader lock that ADR-0007 already uses for the engine),
so two instances never purge concurrently.

**When it runs.** Once a day at `Retention:RunAt` in `Retention:TimeZone`, scheduled through
`TimeProvider` (ADR-0004) so tests control time. A missed run is caught up at the next start.

**What it does, per category:**

1. Compute the cutoff as `TimeProvider.GetUtcNow()` minus the configured period.
2. Select expired rows in batches (for example 500 per batch), each batch in its own transaction.
3. Apply the action from the schedule: aggregate then delete, delete, or write a chain checkpoint
   then delete.
4. Count what was processed.

The job is **idempotent**: running it twice with the same clock changes nothing the second time.
It never touches a row newer than its cutoff. A failure rolls back the current batch only; the
next run continues from where it stopped.

**How it proves it ran.** Every run, successful or not, writes an audit entry with the system as
the actor and no personal data:

| Field | Example |
|---|---|
| Run ID | `7f3c…` |
| Started, finished | `2026-10-20T06:00:00Z`, `2026-10-20T06:00:04Z` |
| Per category: cutoff and rows processed | `PassengerRequests: cutoff 2026-07-22, aggregated 1 204, deleted 1 204` |
| Outcome | `Succeeded`, `PartiallySucceeded` (with the failing category) or `Failed` |

Because these entries are part of the hash chain, a run record cannot be removed or edited
without the verification endpoint detecting it. In addition:

- a metric exposes the time of the last successful run
- the health check reports `Degraded` when the last successful run is older than two days, and
  the operations alert fires
- the admin panel shows the last runs and their counts

**How it is tested** (ADR-0010): an integration test against a real PostgreSQL (Testcontainers)
seeds data on both sides of each cutoff, advances a `FakeTimeProvider`, runs the job and asserts
that only expired data was aggregated, purged or checkpointed, that a second run is a no-op, and
that the run record was written.

## 4. Audit tombstones and the hash chain

The audit trail is append-only and hash-chained: each entry's hash covers the previous entry's
hash, so changing or removing any entry breaks verification of everything after it. Personal
data, on the other hand, must be removable. The design below reconciles the two. ADR-0009 fixes
the outcome (tombstones that keep the chain verifiable); the exact mechanism here is the proposal
for phase 4.

**Separate the personal part from what the chain covers.** Each entry stores:

| Part | Fields | Covered by the chain |
|---|---|---|
| Non-personal | sequence number, timestamp, action, target, outcome | Directly |
| Personal | staff ID, pseudonymised IP | Only through a **personal digest** |
| Personal digest | `SHA-256(salt ‖ canonical personal fields)`, with a random 32-byte salt per entry | Yes |
| Chain | `entryHash = SHA-256(previousHash ‖ non-personal fields ‖ personal digest)` | Yes |

The per-entry salt matters: without it, the digest of a staff ID could be recomputed by trying
every known ID, which would make the "anonymised" entry linkable again.

**Tombstoning an entry** (on a deletion request):

1. Delete the personal fields and the salt.
2. Mark the entry as a tombstone, with the time and the reference of the request that caused it.
   The personal digest, the non-personal fields and the entry hash stay as they were.
3. Append a new audit entry recording that *n* entries were anonymised for request *r*.

Verification recomputes the personal digest when the personal fields are present and uses the
stored digest when the entry is a tombstone. Either way `entryHash` matches, so the chain still
verifies, and anyone reading it can see which entries were anonymised and when. With the salt
gone, the digest cannot be reversed or matched against a guessed staff ID.

**Purging at the end of the retention period.** The job only removes the oldest entries (a prefix
of the chain), never entries in the middle. Before deleting, it appends a **checkpoint** entry
holding the sequence number and hash of the last entry it is about to remove. Verification then
starts from the most recent checkpoint instead of the first entry ever written.

**Interaction with investigations.** Tombstoning entries that are still inside their retention
period can remove evidence a pending investigation needs. How that is handled is an open decision
for the controller ([RIPD, risk R9 and item O1](ripd.md#7-open-items-for-the-controller)).

## 5. Changing a retention period

1. The controller decides the new period and records why.
2. The pull request updates the configuration default, the classification table in the
   `security-lgpd` skill, this schedule, the [ROPA](ropa.md) and the [privacy notice](privacy-notice.md).
3. A shorter period takes effect at the next run. A longer period applies only to data not yet
   purged; nothing deleted can be restored.

## Revision history

| Date | Change | Reviewed by |
|---|---|---|
| 2026-10-19 | First draft, written ahead of phases 4 and 5 | Engineering; pending controller and counsel review |
