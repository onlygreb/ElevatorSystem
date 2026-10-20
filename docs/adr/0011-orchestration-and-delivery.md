# 0011. Local orchestration and container delivery

- **Status:** Accepted; amended by [0029](0029-docker-compose-and-dockerfile.md) (Compose and a Dockerfile for Docker-only runs)
- **Date:** 2026-10-19

## Context

Running the full system needs the API, the engine, RabbitMQ and PostgreSQL, wired together with
correct configuration and with telemetry visible across process boundaries. Deliverables must be
reproducible, minimal and traceable to the commit that produced them.

## Decision

- **Local orchestration with Aspire.** `ElevatorSystem.AppHost` declares every service and backing
  resource. A single `dotnet run` starts the stack with the Aspire dashboard (logs, traces,
  metrics). Service defaults (OpenTelemetry, health checks, resilience) live in
  `ElevatorSystem.ServiceDefaults`.
- **Container images built by the .NET SDK** (`dotnet publish -t:PublishContainer`), with no
  Dockerfiles:
  - based on Ubuntu **chiseled** images (`noble-chiseled-extra`: no shell, no package manager,
    ICU included for pt-BR)
  - run as a **non-root** user by default
- **Delivery on version tags.** Pushing `vX.Y.Z` runs the release workflow:
  - it runs the full test suite
  - it publishes the API and engine images to GitHub Container Registry
  - it attaches signed **build provenance attestations** to them
  - it creates a GitHub release with generated notes

## Alternatives considered

- **Hand-written Docker Compose for development:** works, but duplicates configuration Aspire
  already knows and gives no integrated telemetry dashboard.
- **Dockerfiles:** more control, but more surface to maintain and secure. The SDK produces smaller,
  non-root images by default.
- **Deploying to a specific cloud now:** no target environment is defined yet. Signed images in a
  registry are the portable hand-off point for any orchestrator.

## Consequences

- Anyone with the .NET SDK and Docker can run the whole system with one command.
- Every released image can be verified (`gh attestation verify`) back to the workflow run and commit.
- Production configuration (secrets, hosts, TLS termination) is supplied by the deployment
  environment, never baked into images.
