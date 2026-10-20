// Load test for the elevator API (k6). Proves the system requirement: 100+ concurrent requests, with the
// time from request to acceptance (the call is validated, published and assigned an id) under 100 ms at p95.
//
// Run against an API whose rate limits are raised for the test (see tests/load/README.md):
//   k6 run -e BASE_URL=http://localhost:5251 tests/load/elevator-load.js
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5251';
const LOWEST = 1;
const HIGHEST = 10;
const BURST_VUS = Number(__ENV.BURST_VUS || 200);
const RATE = Number(__ENV.RATE || 2000);
const DURATION = __ENV.DURATION || '30s';

const acceptance = new Trend('elevator_acceptance_ms', true);
const busy = new Counter('elevator_busy_refusals');

export const options = {
  scenarios: {
    // 200 people press a button at the same instant, five times over: the concurrency the requirement names.
    burst: {
      executor: 'per-vu-iterations',
      vus: BURST_VUS,
      iterations: 5,
      maxDuration: '20s',
      exec: 'pressButtons',
    },
    // Then a sustained 2,000 presses a second, hall and car calls, far beyond any building's traffic.
    sustained: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 150,
      maxVUs: 400,
      startTime: '5s',
      exec: 'pressButtons',
    },
    // Passengers arriving on the lowest floor and calling the car, alongside the button pressers.
    passengers: {
      executor: 'constant-arrival-rate',
      rate: 20,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 20,
      startTime: '5s',
      exec: 'arriveAndCall',
    },
  },
  thresholds: {
    'elevator_acceptance_ms': ['p(95)<100'],
    'http_req_failed': ['rate<0.01'],
    'checks': ['rate>0.99'],
    // 503 Busy means the engine's command buffer was full: the API refused rather than lose a call.
    'elevator_busy_refusals': ['count==0'],
  },
};

const json = { headers: { 'Content-Type': 'application/json' } };

function randomFloor() {
  return LOWEST + Math.floor(Math.random() * (HIGHEST - LOWEST + 1));
}

function accepted(response, name) {
  acceptance.add(response.timings.duration, { name });
  if (response.status === 503) {
    busy.add(1, { name });
  }

  return check(response, { [`${name} accepted`]: (r) => r.status === 202 });
}

export function pressButtons() {
  if (Math.random() < 0.5) {
    const floor = randomFloor();
    const direction = floor === HIGHEST ? 'Down' : floor === LOWEST ? 'Up' : (Math.random() < 0.5 ? 'Up' : 'Down');
    const response = http.post(`${BASE_URL}/api/v1/hall-calls`, JSON.stringify({ floor, direction }), { ...json, tags: { name: 'hall-call' } });
    accepted(response, 'hall call');
  } else {
    const response = http.post(`${BASE_URL}/api/v1/car-calls`, JSON.stringify({ floor: randomFloor() }), { ...json, tags: { name: 'car-call' } });
    accepted(response, 'car call');
  }
}

export function arriveAndCall() {
  const joined = http.post(`${BASE_URL}/api/v1/passengers`, '{}', { ...json, tags: { name: 'join' } });
  if (!accepted(joined, 'join')) {
    return;
  }

  const passengerId = joined.json('passengerId');
  const call = http.post(
    `${BASE_URL}/api/v1/passengers/${passengerId}/hall-call`,
    JSON.stringify({ direction: 'Up' }),
    { ...json, tags: { name: 'passenger-call' } });
  accepted(call, 'passenger call');
}
