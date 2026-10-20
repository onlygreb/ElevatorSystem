// Load test for the elevator API (k6). Proves the system requirement: 100+ concurrent requests, with the
// time from request to acceptance (the call is validated, published and assigned an id) under 100 ms at p95.
//
// Run against an API whose rate limits are raised for the test (see tests/load/README.md):
//   k6 run -e BASE_URL=http://localhost:5251 tests/load/elevator-load.js
import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5251';
const LOWEST = 1;
const HIGHEST = 10;
const VUS = Number(__ENV.VUS || 120);
const DURATION = __ENV.DURATION || '30s';

const acceptance = new Trend('elevator_acceptance_ms', true);

export const options = {
  scenarios: {
    // Callers pressing buttons as fast as they can: hall calls from every floor and car calls.
    calls: {
      executor: 'constant-vus',
      vus: VUS,
      duration: DURATION,
      exec: 'pressButtons',
    },
    // Passengers arriving on the lowest floor and calling the car, alongside the button pressers.
    passengers: {
      executor: 'constant-arrival-rate',
      rate: 20,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 20,
      exec: 'arriveAndCall',
    },
  },
  thresholds: {
    'elevator_acceptance_ms': ['p(95)<100'],
    'http_req_failed': ['rate<0.01'],
    'checks': ['rate>0.99'],
  },
};

const json = { headers: { 'Content-Type': 'application/json' } };

function randomFloor() {
  return LOWEST + Math.floor(Math.random() * (HIGHEST - LOWEST + 1));
}

function accepted(response, name) {
  acceptance.add(response.timings.duration, { name });
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
