# LGPD documentation

> [!IMPORTANT]
> These documents are **system documentation drafts** written by the engineering team. They
> describe how ElevatorSystem processes personal data so that the **data controller**
> (controlador) and their **legal counsel** can validate, complete and adopt them. They are not
> legal advice. Every value marked `{{LIKE_THIS}}` is a placeholder the controller must fill in,
> and every retention period is a configurable default the controller must confirm.

ElevatorSystem is built to comply with the LGPD (Lei Geral de Proteção de Dados Pessoais,
Lei nº 13.709/2018). The engineering decision behind it is
[ADR-0009: Privacy by design for LGPD](../adr/0009-privacy-by-design-for-lgpd.md). This folder
turns that decision into the documents the law and the ANPD (Autoridade Nacional de Proteção de
Dados) expect a controller to keep.

## Documents

| Document | LGPD reference | What it answers | Main audience |
|---|---|---|---|
| [Record of processing operations](ropa.md) (Registro das Operações de Tratamento) | Art. 37 | What personal data the system processes, why, on which legal basis, for how long, and who receives it | Controller, encarregado, ANPD on request |
| [Data protection impact report](ripd.md) (Relatório de Impacto à Proteção de Dados Pessoais) | Art. 5 XVII, Art. 10 §3, Art. 38 | Whether the processing is necessary and proportionate, what could harm data subjects, and how the design reduces it | Controller, encarregado, ANPD on request |
| [Retention schedule](retention.md) (tabela de temporalidade) | Art. 15, Art. 16 | How long each data category is kept, what happens at expiry, and how the retention job proves it ran | Controller, operators, auditors |
| [Incident response runbook](incident-response.md) | Art. 48, Resolution CD/ANPD nº 15/2024 | What to do when personal data may have been exposed, and how to notify the ANPD and data subjects within 3 business days | On-call engineers, encarregado, controller |
| [Privacy notice](privacy-notice.md) (aviso de privacidade), en-US and pt-BR | Art. 6 VI, Art. 9, Art. 18, Art. 41 §1 | The text the UI's privacy page shows to staff and passengers | Data subjects |

## How the documents relate to ADR-0009

ADR-0009 makes five design commitments. Each one is described, justified or operated by a
document here:

| ADR-0009 commitment | Where it is documented |
|---|---|
| Passengers are anonymous by design (Art. 6 III, necessity) | [ROPA, operation 4](ropa.md#4-passenger-requests-not-personal-data); [RIPD, necessity and proportionality](ripd.md#3-necessity-and-proportionality) |
| Personal data is limited to staff accounts and the audit trail, each with a legal basis and retention period | [ROPA, operations 1 and 2](ropa.md#1-staff-accounts-and-authentication); [retention schedule](retention.md#1-schedule) |
| Logs and traces contain no personal data | [ROPA, operation 3](ropa.md#3-technical-logs-traces-and-metrics); [RIPD, risk R5](ripd.md#4-risks-to-data-subjects) |
| Retention is enforced by a scheduled job | [Retention schedule, the retention job](retention.md#3-the-retention-job) |
| Data subject rights: export and deletion, with audit tombstones that keep the hash chain verifiable | [Privacy notice, rights](privacy-notice.md#your-rights); [retention schedule, tombstones](retention.md#4-audit-tombstones-and-the-hash-chain) |
| Transparency and accountability: privacy notice, encarregado contact, ROPA, RIPD, incident runbook | This folder |

The data classification table in the `security-lgpd` skill
([`.claude/skills/security-lgpd/SKILL.md`](../../.claude/skills/security-lgpd/SKILL.md)) is the
engineering source of truth for categories, legal bases and default retention. These documents
expand it and must not contradict it.

## Status by delivery phase

Most of the processing described here does not exist yet. The documents are written ahead of the
code so that each phase can be checked against them.

| Phase | Personal data introduced | Documents to re-check when it lands |
|---|---|---|
| 1. Domain and console demo | None | None |
| 2. REST API, SignalR, passenger view | None stored. Client IP addresses are used in memory for rate limiting only | ROPA operation 4, privacy notice (cookies) |
| 3. RabbitMQ, PostgreSQL, recovery | None (snapshots hold elevator state only) | ROPA operation 4, retention (passenger requests) |
| 4. Authentication, roles, admin panel, audit log | Staff accounts, audit trail with pseudonymised IP | All documents |
| 5. LGPD features, observability | Export, deletion, retention job, privacy page, log redaction tests | All documents; finalise and have the controller sign off |

## Keeping the documents current

- Adding or changing a personal field updates the classification table, the [ROPA](ropa.md) and
  the [RIPD](ripd.md) **in the same pull request** (ADR-0009).
- Changing a retention default updates the [retention schedule](retention.md) and the
  [privacy notice](privacy-notice.md).
- Adding a processor or a sub-processor (hosting, e-mail, telemetry backend) updates the
  recipients and international transfer sections of the [ROPA](ropa.md).
- The controller reviews the whole pack at least once a year and after every incident that
  required notification. Record each review in the revision table at the end of each document.
