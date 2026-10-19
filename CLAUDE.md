# ElevatorSystem

Elevator control system: one elevator serving floors 1–10, driven through a REST API and a live
Blazor UI, with RabbitMQ between the API and the control engine. Security, LGPD compliance and
test depth are core requirements, not extras. Read `README.md` for the product view and
`docs/adr/` for every decision already made.

## Layout

| Path | Role |
|---|---|
| `src/ElevatorSystem.Domain` | Elevator state machine, requests, scheduling strategies. No dependencies. |
| `src/ElevatorSystem.Contracts` | HTTP DTOs and message contracts. No dependencies. |
| `src/ElevatorSystem.Application` | `ElevatorController`, use cases, ports (interfaces). |
| `src/ElevatorSystem.Infrastructure` | RabbitMQ, PostgreSQL (EF Core), audit log, retention jobs. |
| `src/ElevatorSystem.Engine` | Host that runs the control loop. Health endpoints only. |
| `src/ElevatorSystem.Api` | REST API, SignalR hub, auth; serves the Blazor UI. |
| `src/ElevatorSystem.Web` | Blazor WebAssembly UI (passenger view + admin panel). |
| `src/ElevatorSystem.ConsoleDemo` | Core scenario with no infrastructure. |
| `src/ElevatorSystem.ServiceDefaults` | OpenTelemetry, health checks, HTTP resilience. |
| `src/ElevatorSystem.AppHost` | Aspire orchestration for local runs. |
| `tests/` | One project per layer; see the `testing` skill. |

## Commands

```bash
dotnet build ElevatorSystem.slnx
dotnet test --solution ElevatorSystem.slnx
dotnet format ElevatorSystem.slnx --verify-no-changes
dotnet run --project src/ElevatorSystem.AppHost       # whole stack + Aspire dashboard (needs Docker from phase 3)
dotnet run --project src/ElevatorSystem.ConsoleDemo   # core only
```

## Rules that are enforced (don't work around them)

- **Dependency direction** is tested by `tests/ElevatorSystem.Architecture.Tests`. To change it,
  write an ADR and update `AllowedReferences` in the same commit.
- **Package versions** live only in `Directory.Packages.props` (the Aspire SDK version in the
  AppHost `Sdk` attribute is the one exception). Stable releases only. MediatR, AutoMapper,
  MassTransit, FluentAssertions and Moq are denied (ADR-0008).
- **Vulnerable packages fail restore.** Fix by upgrading, or pin the patched version under
  "Transitive security pins" in `Directory.Packages.props`. Never lower `NuGetAuditLevel`.
- **Lock files**: after changing packages run `dotnet restore --force-evaluate` and commit the
  updated `packages.lock.json` files; CI restores in locked mode.
- **Time** comes from `TimeProvider`. `DateTime.Now`, `Task.Delay(int)`, `Thread.Sleep` and
  friends are banned in `src/` by analyzer (`src/BannedSymbols.txt`).
- **Warnings are errors.** Don't add `NoWarn` or `#pragma` without a comment that says why.
- **Formatting**: LF line endings, UTF-8 without BOM; `dotnet format` must report no changes.
- **Privacy**: passenger requests carry no personal data, and personal data never reaches logs (ADR-0009).

## Working agreements

- `main` changes only through pull requests. Branch from an up-to-date `main` as
  `<type>/<short-description>` (for example `feat/elevator-state-machine`), push the branch,
  open a PR that fills in the template, and merge only when CI and CodeQL are green.
  Dependabot PRs follow the same rule. Keep each PR to one concern so it stays reviewable.
- Commits follow Conventional Commits (`feat`, `fix`, `test`, `docs`, `build`, `ci`, `refactor`,
  `chore`), one logical change each, code and its tests together.
- Public API names in the domain mirror the system requirements (`MoveUp`, `OpenDoor`,
  `AddRequest`, `RequestElevator`, `ProcessRequests`…). Keep them.
- A behaviour change without a test is not done. A security- or privacy-relevant change also
  updates `docs/security` or `docs/lgpd`.

## Skills

Load the matching skill before working in an area:

- `elevator-domain`: states and transitions, FIFO behaviour, concurrency model, fail-safe rules
- `api-backend`: endpoint checklist, validation, errors, authorization, rate limiting
- `messaging`: RabbitMQ topology, delivery guarantees, idempotency, leader election
- `blazor-ui`: UI structure, components, accessibility, localisation (en-US, pt-BR)
- `security-lgpd`: security checklist, data classification, LGPD obligations
- `testing`: which test type to write, conventions, tools and commands

## Delivery phases

0. Foundation: solution, build policies, CI/CD, ADRs ✅
1. Domain, `ElevatorController` with trip watchdog, console demo, domain and controller test suites ✅
2. REST API, SignalR, Blazor passenger view (in-memory bus)
3. RabbitMQ, PostgreSQL, persistence and recovery, failover
4. Authentication, roles, admin panel, tamper-evident audit log
5. LGPD features and documents, observability, load, end-to-end and mutation testing
