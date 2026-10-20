# Load test

[`elevator-load.js`](elevator-load.js) is a [k6](https://k6.io) script that proves the concurrency requirement:
**100+ simultaneous requests, with the time from request to acceptance under 100 ms at the 95th percentile.**
Acceptance means the API validated the call, handed it to the engine and returned its request id (`202 Accepted`).

Three scenarios:

| Scenario | What it does |
|---|---|
| `burst` | 200 virtual users press a button at the same instant, five times over: the concurrency the requirement names. |
| `sustained` | Then, for 30 seconds, 2,000 presses a second (hall calls from every floor in valid directions, and car calls), far beyond any building's traffic. |
| `passengers` | Alongside, 20 passengers a second enter the building on floor 1 and call the car. |

The run fails if p95 acceptance reaches 100 ms, if more than 1% of requests fail, if under 99% are accepted, or if
the API answers even once with `503` because the engine's command buffer was full.

### When the engine cannot keep up

The API never claims a call it could not hand over (ADR-0003). When the engine's command buffer is full, a call is
refused with `503 Service Unavailable`, the `Busy` reason and `Retry-After`, and the caller presses again. A tight
loop of 140 users with no pause reached that point on a GitHub runner (about 9,000 presses a second); the test
measures a sustained rate well inside it instead, and counts any such refusal as a failure.

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

`BURST_VUS`, `RATE` and `DURATION` change the size of the run (`-e RATE=3000 -e DURATION=2m`).

The [Quality workflow](../../.github/workflows/quality.yml) runs the same test weekly and on demand.

## Last local result

API in single-process mode (in-memory messaging), Release build, on a desktop machine:

| Metric | Result |
|---|---|
| Requests | 61,987 (burst, then about 2,000 per second) |
| Acceptance p95 | 26.9 ms |
| Acceptance median | 1.1 ms |
| `503 Busy` refusals | 0 |
| Failed requests | 0 |

Under Docker Desktop, k6 can report a negative minimum and a single maximum of several seconds: the VM's clock is
adjusted mid-run. They are not request times; the percentiles are unaffected.

The broker-backed stack adds a publisher confirm per call; measure it the same way against the AppHost's `api`
endpoint before relying on these numbers there.
