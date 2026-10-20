# Docker Compose

Runs the whole system with Docker alone, without the .NET SDK or Aspire ([ADR-0029](../docs/adr/0029-docker-compose-and-dockerfile.md)):

| Service | What it is | Address |
|---|---|---|
| `api` | REST API, SignalR hub and the web UI (passenger and admin views) | https://localhost:8443 (http://localhost:8080 redirects) |
| `engine` × 2 | The control loop; one leads, the other stands by and takes over | internal |
| `rabbitmq` | Broker between the API and the engines, with its management UI | http://localhost:15672 |
| `postgres` | The elevator's state, deduplication, leader lease, staff accounts and audit trail | internal |
| `dashboard` | Aspire dashboard: logs, traces and metrics of the API and the engines | http://localhost:18888 |

The images are built by [`Dockerfile`](Dockerfile) on the same chiseled runtime as the released images: no shell, no
package manager, non-root. Data lives in the `rabbitmq-data` and `postgres-data` volumes.

## First run

1. **Secrets.** Copy `.env.example` to `.env` (git-ignored) and fill it in. Each password must be your own; generate
   the audit key with `openssl rand -base64 33`. Compose refuses to start while a value is missing.
2. **HTTPS certificate.** The API serves HTTPS because the staff cookies are `__Host-` and `Secure`. Export one to
   `certs/elevator.pfx` (git-ignored) with the password you put in `CERTIFICATE_PASSWORD`:
   - with the .NET SDK, the development certificate, which your browser already trusts after
     `dotnet dev-certs https --trust`:
     ```bash
     dotnet dev-certs https -ep deploy/certs/elevator.pfx -p <CERTIFICATE_PASSWORD>
     ```
   - without it, a self-signed one (the browser warns once):
     ```bash
     openssl req -x509 -newkey rsa:2048 -nodes -days 365 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost" -keyout deploy/certs/key.pem -out deploy/certs/cert.pem
     ```
     ```bash
     openssl pkcs12 -export -inkey deploy/certs/key.pem -in deploy/certs/cert.pem -out deploy/certs/elevator.pfx -passout pass:<CERTIFICATE_PASSWORD>
     ```
3. **Start**, from the repository root:
   ```bash
   docker compose -f deploy/compose.yaml up --build --detach
   ```

Open https://localhost:8443 to ride the elevator. For the admin view, open `/admin` and either:
- sign in as the test admin, `tester@elevator.local` with `elevator-test-account` (from `.env.example`), with no
  authenticator app; it is a test account with a public password, so set `TEST_ADMIN_PASSWORD` empty wherever others
  can reach the system (ADR-0030); or
- sign in as `ADMIN_EMAIL` with `ADMIN_PASSWORD`; the first sign-in asks you to register an authenticator app and shows
  recovery codes.

Then start a demo scenario and watch the building fill up.

## Day to day

```bash
docker compose -f deploy/compose.yaml logs -f api engine
```

```bash
docker compose -f deploy/compose.yaml down
```

`down` keeps the volumes, so the car's floor, the staff accounts and the audit trail survive; `down --volumes`
starts over. To see failover, stop the engine that leads (the one whose log says it became the leader):

```bash
docker compose -f deploy/compose.yaml stop <container name>
```

The other engine takes over from the last saved state within a few seconds.

## Compared with Aspire

`dotnet run --project src/ElevatorSystem.AppHost` remains the development path: it generates the secrets, wires the
same services and opens the dashboard for you. Compose is for running the system where only Docker is installed. Both
run the same configuration; when one changes, change the other.
