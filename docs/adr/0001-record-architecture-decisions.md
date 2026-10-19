# 0001. Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

The system combines a safety-sensitive control loop, a message broker, a web API, a browser UI and
privacy obligations under the LGPD. Many choices only make sense with their context (why a lock
here, why no Kafka, why passengers are anonymous). That context gets lost if it lives only in pull
requests or in people's heads.

## Decision

Record every significant architectural decision as a numbered Markdown file in `docs/adr/`, in a
lightweight Context / Decision / Alternatives / Consequences format. Records are immutable once
accepted. A change of direction is a new record that supersedes the old one.

## Alternatives considered

- **Wiki pages:** they drift away from the code and aren't reviewed with it.
- **Comments only:** good for local reasoning, but they can't explain cross-cutting decisions.

## Consequences

- Reviewers can trace the reason behind any structural rule, and the architecture tests point to
  the ADR they enforce.
- Writing an ADR is part of any change that alters a rule recorded here.
