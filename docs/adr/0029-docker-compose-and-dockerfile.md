# 0029. Run the whole system with Docker Compose, built by a Dockerfile

- **Status:** Accepted (amended by [0034](0034-docker-compose-starts-with-no-setup.md): no setup needed, local defaults bound to this machine)
- **Date:** 2026-10-20
- **Amends:** [0011](0011-orchestration-and-delivery.md)

## Context

ADR-0011 made Aspire the way to run the system locally and the .NET SDK the way to build images, and it
rejected both a hand-written Compose file (it duplicates what Aspire wires) and Dockerfiles (more to maintain).
Both choices assume the .NET SDK on the machine. People who only have Docker, and environments that run
Compose rather than Aspire, need the whole system from a single `docker compose up`.

## Decision

1. **`deploy/compose.yaml`** runs everything: RabbitMQ and PostgreSQL (the same images and versions as the
   Aspire host and the tests), two engines that compete for the leader lease, the API with the web UI, and the
   standalone Aspire dashboard receiving the OTLP telemetry of the API and the engines.
2. **`deploy/Dockerfile`** builds the API and the engine (`--target api`, `--target engine`) in the SDK image
   with a locked restore, onto the same `noble-chiseled-extra` runtime as the released images: no shell, no
   package manager, non-root. Released images are still built by the SDK and signed (ADR-0011).
3. **Production settings, as a deployment would use them.** `ASPNETCORE_ENVIRONMENT=Production`, distributed
   mode, and HTTPS on port 8443 from a certificate mounted from `deploy/certs/` (git-ignored), because the staff
   cookies are `__Host-` and `Secure` (ADR-0022). Plain HTTP on 8080 redirects to it.
4. **Secrets only from `deploy/.env`** (git-ignored, with a committed `.env.example`). Compose refuses to start
   while one is missing; nothing secret is in the images or the repository.
5. **Readiness by the infrastructure's own checks.** RabbitMQ (`check_port_connectivity`) and PostgreSQL
   (`pg_isready`) have health checks the .NET services wait for. The chiseled images have no shell for a health
   command; the services retry their connections themselves (ADR-0018, ADR-0019).
6. Npgsql is told not to probe for GSS encryption: the chiseled image has no Kerberos library.
7. CI builds both targets and validates the Compose file on every pull request, so neither drifts from the code.

## Alternatives considered

- **Images from GitHub Container Registry only** — needs a release for every change and leaves a contributor
  without a way to run the current branch.
- **SDK-built images plus a Compose file** — keeps ADR-0011's single build path, but then Compose needs the .NET
  SDK to build first, which is what this decision removes.
- **A reverse proxy for TLS** — one more service and certificate authority to explain; Kestrel serves HTTPS
  directly with a mounted certificate.

## Consequences

- `docker compose -f deploy/compose.yaml up --build` runs the system with Docker alone.
- There are two build paths for the same images (SDK for releases, Dockerfile for Compose), and two
  descriptions of the stack (the AppHost and the Compose file). When one changes, the other must change too;
  `deploy/README.md` says so, and the CI job catches build breaks but not configuration drift.
- A certificate must be provided before the first run: the developer certificate exported by `dotnet dev-certs`,
  or a self-signed one made with OpenSSL.
