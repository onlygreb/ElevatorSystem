# Personal data incident response runbook

> [!IMPORTANT]
> System documentation draft for the data controller (controlador) and their legal counsel to
> validate. Not legal advice. The deadlines and criteria below summarise LGPD Art. 48 and
> Resolution CD/ANPD nº 15/2024 (Regulamento de Comunicação de Incidente de Segurança); counsel
> must confirm them against the current text before the runbook is adopted. Placeholders look
> like `{{LIKE_THIS}}`.

This runbook covers **security incidents involving personal data** (incidentes de segurança com
dados pessoais): any confirmed adverse event that compromises the confidentiality, integrity,
availability or authenticity of personal data the system holds. To report a vulnerability that
has not (yet) caused an incident, follow [SECURITY.md](../../SECURITY.md); if the report shows
personal data may have been exposed, this runbook starts.

## 1. What could be affected in this system

Passenger requests contain no personal data, so incidents that affect only the elevator, the
broker or passenger history are operational incidents, not personal data incidents. Personal
data, and the secrets that protect it, exist only here:

| Asset | Personal data | Why it matters |
|---|---|---|
| Identity store (PostgreSQL) | Staff names, e-mails, roles, password hashes, TOTP secrets, recovery codes | **Authentication data**, a relevant-risk criterion under Resolution CD/ANPD nº 15/2024 |
| Audit trail (PostgreSQL) | Staff IDs, actions, timestamps, IP pseudonyms | Integrity matters as much as confidentiality |
| Database backups | All of the above | Often forgotten in containment |
| Data Protection key ring | None itself; decrypts TOTP secrets and session cookies | Its exposure turns encrypted data into clear data |
| HMAC key for IP pseudonyms | None itself; lets pseudonyms be recomputed from candidate IPs | Its exposure weakens pseudonymisation |
| Logs, traces, metrics | Should be none; any personal value found there is itself an incident | Wider audience, possibly another country |

## 2. Deadlines at a glance

| What | Deadline | Source |
|---|---|---|
| Processor (operador) informs the controller | `{{PROCESSOR_NOTICE_DEADLINE}}` per the data processing agreement (recommended: 24 hours) | Art. 39, contract |
| Communication to the **ANPD**, when there is relevant risk or damage | **3 business days** (dias úteis) from the moment the controller became aware that the incident affected personal data | Art. 48; Resolution CD/ANPD nº 15/2024 |
| Communication to **affected data subjects**, in the same case | **3 business days** from the same moment | Art. 48; Resolution CD/ANPD nº 15/2024 |
| Complementary information to the ANPD, when not all facts were known at first | 20 business days from the initial communication | Resolution CD/ANPD nº 15/2024 |
| Incident record, **including incidents that were not communicated** | Kept for at least 5 years | Resolution CD/ANPD nº 15/2024 |

Small processing agents (agentes de tratamento de pequeno porte, Resolution CD/ANPD nº 2/2022)
may have longer deadlines; counsel must confirm whether the controller qualifies. Do not plan on
it.

**The clock starts when the controller knows personal data was affected, not when the
investigation ends.** Record that moment in the incident record as soon as it happens.

## 3. Roles

| Role | Who | Responsibilities |
|---|---|---|
| Incident lead | On-call engineer `{{ON_CALL_CONTACT}}` | Opens the incident, leads containment and investigation, keeps the timeline |
| Encarregado (DPO) | `{{DPO_NAME}}`, `{{DPO_EMAIL}}` | Runs the risk assessment, drafts communications, is the contact point for the ANPD and data subjects (Art. 41) |
| Controller representative | `{{CONTROLLER_REPRESENTATIVE}}` | Decides whether to communicate, signs the communications, approves resources |
| Legal counsel | `{{LEGAL_COUNSEL_CONTACT}}` | Confirms the legal assessment and deadlines, reviews communications |
| Processors | `{{HOSTING_PROVIDER_CONTACT}}`, `{{SUPPORT_PROVIDER_CONTACT}}` | Inform the controller, preserve evidence, support containment |

One person can hold more than one role in a small organisation, but the incident lead must not
also be the person who decides on communication.

## 4. Detection and reporting

Typical triggers in this system:

- the audit trail verification endpoint reports a broken hash chain
- unusual sign-in failures, lockouts or 2FA resets in the audit trail; privileged actions at
  unusual times
- a personal value found in logs, traces, metrics or an error response
- an alert from the hosting provider, the database or the secret store
- a vulnerability report (SECURITY.md), a Dependabot or CodeQL alert for a component that handles
  personal data, or a compromised dependency
- a staff member reporting a lost device, a phishing attempt or an action they did not perform
- a processor's notice

Whoever notices it reports it immediately to the incident lead at `{{INCIDENT_REPORT_CHANNEL}}`,
even when unsure. A false alarm costs minutes; a late report can cost the deadline.

## 5. Response steps

### 5.1 Triage (first hour)

1. Open an incident record (template in [section 8](#8-record-keeping)) with the time of the report.
2. Decide whether personal data is or may be affected. If yes, note **the time of awareness**,
   tell the encarregado and the controller representative, and continue with this runbook.
3. Assign the roles in section 3.

### 5.2 Containment

Contain first, but preserve evidence: snapshot affected systems and export the relevant logs and
audit entries before changing anything that would overwrite them. Then, depending on the case:

| Scenario | Containment |
|---|---|
| Staff account compromised | Disable the account; update its security stamp so every session is signed out; reset the password and the TOTP secret; review its recent audit entries |
| Identity store or backup exfiltrated | Rotate database and broker credentials; force a password and 2FA reset for every staff account; check whether the Data Protection key ring was also exposed |
| Data Protection key ring exposed | Generate a new key and revoke the exposed one (this signs everyone out and makes TOTP secrets encrypted with it unreadable, so plan a 2FA re-enrolment); treat the identity store as exposed |
| HMAC key exposed | Rotate the key immediately; record which period's pseudonyms are weakened |
| Personal data found in logs or traces | Purge the affected records from the telemetry backend; fix the classification or redaction; add the value to the log-capture test; check where the backend stores data |
| Audit chain broken | Do not repair it in place; preserve the database; find the first entry that fails verification and investigate who had write access |
| Compromised dependency or pipeline | Follow ADR-0008: pin or remove the package, rebuild, redeploy from a verified image (`gh attestation verify`), rotate any secret the build could read |

Putting the elevator out of service is a safety decision under ADR-0007, not a privacy measure;
it is rarely needed for a personal data incident.

### 5.3 Assessment of risk and damage

The encarregado assesses, with counsel, whether the incident **may cause relevant risk or
damage** to data subjects. Under Resolution CD/ANPD nº 15/2024 that is the case when the
incident may significantly affect the data subjects' interests and fundamental rights **and**
involves at least one of:

- sensitive personal data (Art. 5 II)
- data of children, adolescents or the elderly
- financial data
- **authentication data in systems**
- data protected by legal, judicial or professional secrecy
- data at large scale

For this system:

| Incident | Likely assessment |
|---|---|
| Identity store, backups or Data Protection key ring exposed | Relevant risk: authentication data. Communicate unless counsel concludes otherwise |
| A staff account used by someone else | Usually relevant risk: authentication data, and actions attributed to the victim |
| Audit trail read without authorisation | Assess: pseudonymised data, but it reveals working patterns of identifiable staff |
| Audit trail altered | Assess: integrity incident that may wrongly attribute actions |
| Personal value found in logs, seen only by operations staff, purged quickly | Often not relevant risk; record it in any case |

Record the reasoning either way. A decision **not** to communicate must be justified in the
incident record.

### 5.4 Communication

If there is relevant risk or damage, the controller communicates to the ANPD and to the affected
data subjects within the deadlines in section 2. If not all facts are known yet, communicate what
is known within the deadline and send complementary information later.

- **ANPD**: through the incident communication form on the ANPD website (gov.br/anpd), signed by
  the controller or the encarregado. Template in [section 7.1](#71-communication-to-the-anpd).
- **Data subjects**: individually, by work e-mail or another direct channel, in clear and simple
  language, in Portuguese. Template in [section 7.2](#72-communication-to-data-subjects).
- Do not include more personal data in a communication than it needs.

### 5.5 Recovery and lessons learned

1. Restore service from known-good state. After restoring a backup, re-apply the deletion
   ledger ([retention schedule](retention.md#backups)).
2. Verify the audit chain end to end.
3. Within 10 business days, hold a blameless review: timeline, root cause, what worked, what to
   change. Turn each action into an issue, and update the [RIPD](ripd.md) risk table if the
   incident revealed a new or underrated risk.

## 6. Internal notice (engineering to controller)

```text
Subject: [INCIDENT {{INCIDENT_ID}}] Possible personal data incident in ElevatorSystem

Time of report:         {{REPORT_TIME}}
Time of awareness:      {{AWARENESS_TIME}}  (the 3-business-day clock starts here)
What happened:          {{SHORT_DESCRIPTION}}
Systems affected:       {{SYSTEMS}}
Personal data involved: {{DATA_CATEGORIES}} (or "unknown yet")
Data subjects:          {{NUMBER_OR_ESTIMATE}} staff accounts
Containment so far:     {{ACTIONS_TAKEN}}
Next update:            {{NEXT_UPDATE_TIME}}
Incident lead:          {{NAME_AND_CONTACT}}
```

## 7. Communication templates

These templates are in English for review. Communications to the ANPD and to Brazilian data
subjects must be sent in Portuguese; the controller translates and counsel reviews them.

### 7.1 Communication to the ANPD

The ANPD form asks for at least the items of Art. 48 §1 and Resolution CD/ANPD nº 15/2024.
Prepare the answers in this structure:

| Item | Answer |
|---|---|
| Controller, CNPJ, encarregado contact | `{{CONTROLLER_LEGAL_NAME}}`, `{{CONTROLLER_CNPJ}}`, `{{DPO_NAME}}`, `{{DPO_EMAIL}}` |
| Notifier, if not the encarregado, and capacity | |
| Date and time of the incident, if known; date and time of awareness | |
| Description: what happened and how it was discovered | |
| Nature and categories of personal data affected | for example: name, work e-mail, password hash, encrypted TOTP secret |
| Number of data subjects affected, and their category | for example: 23 staff members (employees and contractors) |
| Whether sensitive data or data of children, adolescents or the elderly was involved | No (the system holds none) |
| Technical and security measures in place before the incident | Hashing and encryption of credentials, IP pseudonymisation, TLS, least privilege, audit trail ([RIPD section 5](ripd.md#5-mitigations)) |
| Risks to data subjects and possible consequences | |
| Measures taken or planned to reverse or mitigate the effects | |
| Whether, when and how data subjects were communicated | |
| Reasons for any delay | |
| Whether the communication is preliminary or complete | |

### 7.2 Communication to data subjects

```text
Subject: Security incident affecting your ElevatorSystem staff account

Dear {{FIRST_NAME}},

On {{AWARENESS_DATE}} we became aware of a security incident that affected
personal data related to your staff account in ElevatorSystem, the system used
to operate the elevator at {{BUILDING_NAME}}.

What happened
{{PLAIN_DESCRIPTION_OF_THE_INCIDENT}}

What data was involved
{{DATA_CATEGORIES}}. The system does not store any sensitive personal data,
and elevator requests are not linked to anyone.

What this could mean for you
{{RISKS_IN_PLAIN_LANGUAGE}}

What we have done
{{MEASURES_TAKEN}}

What we recommend you do
- If you used the same password anywhere else, change it there.
- Set up two-factor authentication again when you next sign in.
- Report any action in the operations panel that you did not perform.

Questions
Contact our data protection officer (encarregado), {{DPO_NAME}},
at {{DPO_EMAIL}}. You can also contact the National Data Protection
Authority (ANPD).

{{CONTROLLER_REPRESENTATIVE}}
{{CONTROLLER_LEGAL_NAME}}
```

## 8. Record keeping

Every incident involving personal data is recorded, **whether or not it was communicated**, and
the record is kept for at least 5 years. Keep it outside the system it describes, with access
limited to the encarregado, the controller representative and counsel.

| Field | Content |
|---|---|
| Incident ID | |
| Date and time of the incident, of the report and of awareness | |
| General description and circumstances | |
| Nature and categories of personal data affected | |
| Number of data subjects affected | |
| Risk and damage assessment, with reasoning | |
| Decision to communicate or not, who decided, and why | |
| Communications sent: to whom, when, how; copies attached | |
| Containment and mitigation measures, with times | |
| Root cause and follow-up actions | |
| Timeline | |

## 9. Keeping the runbook ready

- Review the runbook once a year and after every incident.
- Run a tabletop exercise once a year, for example "the identity store backup is found on a
  public bucket on a Friday evening", and time the path to a ready ANPD communication.
- Keep the contacts in section 3 current; an outdated phone number is the most common failure.

## Revision history

| Date | Change | Reviewed by |
|---|---|---|
| 2026-10-19 | First draft | Engineering; pending controller and counsel review |
