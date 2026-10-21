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

From the repository root, with nothing to set up first (ADR-0034):

```bash
docker compose -f deploy/compose.yaml up --build --detach
```

Open https://localhost:8443 and accept the browser's warning once: on first start the API made itself a self-signed
certificate for localhost, kept in the `api-certificate` volume. For the admin view, open `/admin` and sign in as the
test admin, `tester@elevator.local` with `elevator-test-account`, with no authenticator app (ADR-0030). Then start a
demo scenario and watch the building fill up. To compare scheduling strategies, take the car out of service in the
admin view, switch between FIFO and LOOK there, return it to service and run the same scenario (ADR-0033).

## Your own settings

Every setting has a local default. The defaults are public, so the API listens on this machine only (`API_ADDRESS`).
For anything beyond trying it out, copy [`.env.example`](.env.example) to `.env` (git-ignored) and change what you
need; each value is explained there:

- **Passwords** for RabbitMQ (`MESSAGING_PASSWORD`) and PostgreSQL (`DATABASE_PASSWORD`). Both keep the password they
  were first created with: after changing one, run `down --volumes` once.
- **A real admin**: set `ADMIN_PASSWORD` (at least 12 characters, at least 4 different). It is created once; the
  first sign-in registers an authenticator app and shows recovery codes. Set `TEST_ADMIN_PASSWORD` empty to turn the
  test account off.
- **The audit key** `AUDIT_PSEUDONYM_KEY` (`openssl rand -base64 33`). Without one, client pseudonyms in the audit
  trail link only until the API restarts.
- **Your own HTTPS certificate**: put `elevator.pfx` in `deploy/certs` (git-ignored), set `CERTIFICATE_DIR=./certs`
  and its password in `CERTIFICATE_PASSWORD`. With the .NET SDK, the development certificate is one your browser
  already trusts after `dotnet dev-certs https --trust`:
  ```bash
  dotnet dev-certs https -ep deploy/certs/elevator.pfx -p <CERTIFICATE_PASSWORD>
  ```
- **Another address**: `API_ADDRESS=0.0.0.0` to reach the API from your network, once the passwords are your own.
- **Scheduling**: `ELEVATOR_SCHEDULING=Look` to start the engines with LOOK instead of FIFO.

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
