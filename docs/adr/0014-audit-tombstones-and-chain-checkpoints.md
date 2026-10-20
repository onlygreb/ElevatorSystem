# 0014. Anonymise audit entries with salted personal digests and purge with checkpoints

- **Status:** Accepted (implemented in phase 4, see [0023](0023-audit-trail-writer-and-verification.md); checkpoints arrive with the retention job)
- **Date:** 2026-10-19

## Context

ADR-0009 makes the audit trail tamper-evident (append-only, hash-chained, with a verification
endpoint) and also requires that personal data in it can be removed: a staff member's deletion
request anonymises their entries with a tombstone, and the retention job purges
entries after 1 year. ADR-0009 fixes the outcome, a chain that still verifies, but
not how.

The two requirements pull against each other. If an entry's hash covers its personal fields
directly, removing those fields breaks verification of that entry and of every entry after it.
If the hash does not cover them, they can be changed without detection. And purging the oldest
entries removes the start of the chain that verification walks from.

Two constraints apply to any design:

- A tombstoned entry must be anonymous: the remaining data must not let
  anyone link it back to the staff member by reasonable means. A plain hash of a staff ID fails
  this, because the set of staff IDs is small enough to try them all.
- Cryptography only through the platform (`SHA256`, `RandomNumberGenerator`), per the
  `security` checklist.

The threat model records the problem as T-AUD-02 and the open question as the first half of OQ-14
([`docs/security/threat-model.md`](../security/threat-model.md)).

## Decision

**Split every audit entry into a non-personal part and a personal part**, and let the chain cover
the personal part only through a salted digest:

| Part | Fields | Covered by the chain |
|---|---|---|
| Non-personal | sequence number, timestamp, action, target, outcome | Directly |
| Personal | staff ID, pseudonymised IP | Only through the personal digest |
| Salt | 32 random bytes from `RandomNumberGenerator`, one per entry | No (deleted on tombstoning) |
| Personal digest | `SHA-256(salt ‖ canonical personal fields)` | Yes |
| Entry hash | `SHA-256(previousHash ‖ canonical non-personal fields ‖ personal digest)` | Yes |

**Tombstoning** an entry, on a deletion request:

1. Delete the personal fields and the salt.
2. Mark the entry as a tombstone, with the time and the reference of the request that caused it.
   The personal digest, the non-personal fields and the entry hash are not changed.
3. Append an audit entry recording that *n* entries were anonymised for request *r*.

**Verification** recomputes the personal digest when the personal fields are present and uses the
stored digest when the entry is a tombstone. Either way the entry hash matches. Without the salt,
the stored digest cannot be reversed or matched against a guessed staff ID.

**Purging at the end of the retention period** removes only a prefix of the chain (the oldest
entries), never entries in the middle. Before deleting, the retention job appends a **checkpoint**
entry holding the sequence number and entry hash of the last entry it is about to remove.
Verification starts from the most recent checkpoint instead of the first entry ever written.

Canonical serialisation of the fields (field order, encoding, timestamp format) is fixed in code
and covered by tests with known vectors, so a digest computed today verifies after any refactor.

Phase 4 implemented it as
written (`AuditChain`, `AuditTrail`; the known vectors are in `AuditChainTests`) and accepted it.

Out of scope here, and still open in the threat model: anchoring the chain head outside the
database (second half of OQ-14), who appends to the chain (OQ-4) and the database grants for the
audit table (OQ-7). This design works with any answer to them.

## Alternatives considered

- **Chain over the full entry, rewrite the chain on deletion:** recomputing every later hash
  makes rewriting the chain a normal operation, which is exactly what tamper evidence must
  prevent.
- **Unsalted digest of the personal fields:** keeps the chain verifiable, but a tombstone can be
  re-identified by hashing every known staff ID. So it is not anonymous.
- **HMAC of the personal fields under a secret key** (as OQ-14 suggests): also hides the fields,
  but adds a long-lived key whose leak would make every tombstone guessable again. A random
  per-entry salt that is destroyed on tombstoning gives the same protection with no key to keep.
- **Per-subject encryption keys with crypto-shredding:** works, but needs a key per staff member,
  key storage and rotation, and still leaves ciphertext in the chain. More moving parts for no
  extra guarantee here.
- **Keep the personal data and only hide it in the UI:** not anonymisation; the deletion right
  would not be met.
- **External timestamping or anchoring of chain heads:** stronger evidence against an insider
  rewriting the whole database, but adds a third-party processor. Can be added on top later
  without changing this design.

## Consequences

- Deletion requests and retention purges no longer conflict with tamper evidence: the chain
  verifies before and after both, and tombstones and checkpoints are themselves visible in it.
- Every audit entry stores a salt and a digest (64 extra bytes), and the writer must produce the
  canonical serialisation exactly. Tests with fixed vectors guard it.
- Verification has two code paths (live and tombstoned entries) and starts from a checkpoint;
  both are covered by integration tests, including a tampered tombstone and a removed entry in
  the middle of the chain.
- Tombstoning entries that are still inside their retention period can remove attribution an
  investigation needs. Whether that is deferred or needs a second approval is still the
  controller's decision; this design supports either.
