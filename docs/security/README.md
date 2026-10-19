# Security documentation

This folder holds the security analysis of ElevatorSystem. The decisions it relies on are in the
[architecture decision records](../adr/README.md). How to report a vulnerability is in
[SECURITY.md](../../SECURITY.md), and privacy and LGPD documents go in `docs/lgpd/`.

| Document | What it covers |
|---|---|
| [threat-model.md](threat-model.md) | STRIDE threat model of the planned architecture: assets, actors, data-flow diagram with trust boundaries, threats per element and per data flow, elevator-specific abuse cases, residual risks, open questions |

## What the threat model is for

The threat model connects each threat to the mechanism that stops it, the ADR that decided that
mechanism and the delivery phase that builds it. It does three jobs:

- **Design input.** Before a phase starts, its rows show which controls that phase has to deliver.
- **Review checklist.** A pull request that touches a trust boundary is checked against the rows
  for that boundary.
- **Honest status.** A row is `Implemented` only when the mechanism exists on `main` and a test or
  configuration proves it. Everything else is `Planned` (decided, not built) or `Open` (no
  mitigation decided yet).

## When it is reviewed

The threat model is a living document. It is reviewed and updated:

1. **On every architectural change.** A pull request that adds or supersedes an ADR, adds a
   process, data store, external dependency or network flow, or moves a trust boundary updates
   `threat-model.md` in the same pull request: the data-flow diagram, the affected STRIDE rows and
   any new abuse case. This goes with the pull request template's "Architectural changes are
   recorded in a new ADR" item: a new ADR without a threat-model update is incomplete.
2. **At the start of each delivery phase.** Read the rows for that phase, confirm the planned
   mitigations still fit the design, and turn them into work items (see below).
3. **At the end of each delivery phase.** Move delivered rows from `Planned` to `Implemented`, and
   only for mechanisms that a test or committed configuration proves. Re-check the residual risks
   and the open questions, and record the review in the change log at the end of
   `threat-model.md`.
4. **After a security incident or a vulnerability report.** Add the threat if it was missing,
   correct the mitigation if it failed, and link the advisory.
5. **When a dependency or platform changes how it handles security.** For example, a broker or
   database major version, the identity stack, or the container base image.

Each review is recorded with its date, the trigger and the phase in the change log at the end of
`threat-model.md`.

## How findings become work

| Finding | What happens |
|---|---|
| A threat with no mitigation | Added as an `Open` row and an entry in "Open questions". It becomes a GitHub issue labelled `security` that links the row ID (for example `T-ENG-03`). |
| An open question answered by a design decision | A new ADR (or a superseding one) records the decision. The row changes to `Planned` and cites the ADR and phase. |
| A planned mitigation reached by its phase | The phase's work includes the mechanism **and** a test that proves it (for example an authorization-matrix row, a chaos test, a privacy test; see ADR-0010). The row becomes `Implemented` in the pull request that lands both. |
| A mitigation that turns out not to work | The row goes back to `Open`, and an issue is opened. If it can be exploited in a released version, it is handled as a vulnerability under [SECURITY.md](../../SECURITY.md). |
| A residual risk the project accepts | Stays in "Residual risks" with the assumption it rests on. If that assumption stops being true, the risk becomes an open finding again. |
| A finding that may involve personal data | Also follows the incident-response runbook in `docs/lgpd/` (ANPD notification within 3 business days, ADR-0009). |

Row IDs are stable: a row that no longer applies is marked as removed with the reason, not
renumbered, so issues and pull requests that cite it keep working.
