# Load test

[`elevator-load.js`](elevator-load.js) is a [k6](https://k6.io) script that proves the concurrency requirement:
**100+ simultaneous requests, with the time from request to acceptance under 100 ms at the 95th percentile.**
Acceptance means the API validated the call, handed it to the engine and returned its request id (`202 Accepted`).

Two scenarios run together for 30 seconds:

| Scenario | What it does |
|---|---|
| `calls` | 120 virtual users press buttons non-stop: hall calls from every floor (only valid directions) and car calls. |
| `passengers` | 20 passengers a second enter the building on floor 1 and call the car. |

The run fails if p95 acceptance reaches 100 ms, if more than 1% of requests fail, or if under 99% are accepted.

## Running it

The API's per-client rate limits would turn a load test from one machine into a rate-limit test, so raise them
for the run, and only for the run:

```bash
cd src/ElevatorSystem.Api
dotnet build -c Release
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://0.0.0.0:5391 Hosting__Mode=SingleProcess \
  RateLimiting__Passenger__TokenLimit=1000000 RateLimiting__Passenger__TokensPerPeriod=1000000 \
  RateLimiting__Read__TokenLimit=1000000 RateLimiting__Read__TokensPerPeriod=1000000 \
  RateLimiting__ConcurrentApiRequests=1024 Logging__LogLevel__Default=Warning \
  dotnet bin/Release/net10.0/ElevatorSystem.Api.dll
```

Then, from the repository root, with k6 installed or through Docker:

```bash
k6 run -e BASE_URL=http://localhost:5391 tests/load/elevator-load.js
docker run --rm -v "$PWD/tests/load:/scripts:ro" grafana/k6:2.3.0 run -e BASE_URL=http://host.docker.internal:5391 /scripts/elevator-load.js
```

`VUS` and `DURATION` override the size of the `calls` scenario (`-e VUS=300 -e DURATION=2m`).

The [Quality workflow](../../.github/workflows/quality.yml) runs the same test weekly and on demand.

## Last local result

API in single-process mode (in-memory messaging), Release build, a desktop machine running a mutation test at
the same time:

| Metric | Result |
|---|---|
| Requests | 199,095 in 31 s (about 6,400 per second) |
| Acceptance p95 | 36.9 ms |
| Acceptance median | 15.5 ms |
| Failed requests | 0 |

The broker-backed stack adds a publisher confirm per call; measure it the same way against the AppHost's `api`
endpoint before relying on these numbers there.
