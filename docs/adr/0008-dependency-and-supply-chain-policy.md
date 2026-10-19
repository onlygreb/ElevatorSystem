# 0008. Dependency and supply-chain policy

- **Status:** Accepted
- **Date:** 2026-10-19

## Context

Every dependency is code we run but didn't write. Several popular .NET libraries changed to
commercial licences recently, and package ecosystems are a common attack vector (typosquatting,
dependency confusion, compromised maintainers, vulnerable transitive packages).

## Decision

**Versions and restore**
- Central Package Management: every version is in `Directory.Packages.props`, and transitive
  pinning is on, so a vulnerable transitive package can be overridden in one place.
- Lock files (`packages.lock.json`) are committed. CI restores in locked mode.
- NuGet audit covers all packages, direct and transitive, at every severity. Because warnings are
  errors, a known vulnerability fails the build. The fix is an upgrade or a pin under
  "Transitive security pins", never lowering the audit level.
- `NuGet.config` clears all sources and maps every package to nuget.org (package source mapping).
- Only stable releases.

**Denied packages**

The architecture tests enforce this list:

| Package | Reason | Use instead |
|---|---|---|
| MediatR | Commercial licence since 2025 | Plain handler classes |
| AutoMapper | Commercial licence since 2025 | Explicit mapping methods |
| MassTransit (v9+) | Commercial licence | `RabbitMQ.Client` (ADR-0003) |
| FluentAssertions (v8+) | Commercial licence | Shouldly |
| Moq | SponsorLink harvested developer e-mail hashes (a privacy incident) | Hand-written fakes, NSubstitute |

**GitHub**
- Every third-party action is pinned to a full commit SHA, with the version in a comment.
- Workflows default to `contents: read`, and elevated permissions are granted per job.
- Dependabot opens grouped weekly updates for NuGet and Actions.
- CodeQL scans C# and the workflows.
- Pull requests run a dependency review that fails on vulnerable additions.

## Alternatives considered

- **Floating versions** (`*`, ranges): convenient but not reproducible, and a compromised release
  would flow in automatically.
- **Suppressing advisories** to unblock builds: hides real risk. A pin is just as fast and actually fixes it.

## Consequences

- Restores are reproducible, and a new advisory surfaces as a failing build within a day of
  publication.
- Dependency updates are deliberate pull requests with lock-file changes. If Dependabot can't
  refresh lock files for an update, run `dotnet restore --force-evaluate` on its branch.
