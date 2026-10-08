// k6 load test for the NexaVerify face API: enroll, verify (1:1) and search/identify (1:N).
//
//   dotnet run --project tests/Load/NexaVerify.Load -- fixtures --out tests/Load/k6/fixtures --count 20
//   k6 run -e BASE_URL=https://api.example -e API_KEY=nv_live_xxx tests/Load/k6/faces.js
//
// Pass criteria (docs/01 section 10) are k6 thresholds, so `k6 run` exits non-zero when they are broken:
//   verify p95 <= 800 ms, identify p95 <= 1500 ms, fewer than 1 % unexpected failures.
// 429 answers are NOT failures here: they mean the load tenant's limits were reached (raise api.rateLimitPerMinute /
// api.dailyQuota for the load tenant to measure the engine rather than the limiter).
import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate } from 'k6/metrics';
import exec from 'k6/execution';

const BASE_URL = (__ENV.BASE_URL || 'http://localhost:5101').replace(/\/$/, '');
const API_KEY = __ENV.API_KEY;
const PEOPLE = parseInt(__ENV.PEOPLE || '20', 10);
const VERIFY_RATE = parseInt(__ENV.VERIFY_RATE || '20', 10); // requests per second
const SEARCH_RATE = parseInt(__ENV.SEARCH_RATE || '5', 10);
const ENROLL_RATE = parseInt(__ENV.ENROLL_RATE || '2', 10);
const DURATION = __ENV.DURATION || '1m';

if (!API_KEY) {
  throw new Error('Set -e API_KEY=<an API key with faces.enroll, faces.verify and faces.identify scopes>');
}

// Fixtures written by `NexaVerify.Load fixtures`: person-N.jpg is the enrolment photo, person-N-b.jpg another photo of the same person.
const enrolPhotos = [];
const probePhotos = [];
for (let i = 0; i < PEOPLE; i++) {
  enrolPhotos.push(open(`./fixtures/person-${i}.jpg`, 'b'));
  probePhotos.push(open(`./fixtures/person-${i}-b.jpg`, 'b'));
}

const unexpected = new Rate('unexpected_failures');
const limited = new Counter('rate_limited');

export const options = {
  scenarios: {
    setup_people: { executor: 'shared-iterations', vus: 1, iterations: 1, exec: 'enrolPool', maxDuration: '5m' },
    verify: { executor: 'constant-arrival-rate', rate: VERIFY_RATE, timeUnit: '1s', duration: DURATION, preAllocatedVUs: 20, maxVUs: 200, exec: 'verify', startTime: '30s' },
    search: { executor: 'constant-arrival-rate', rate: SEARCH_RATE, timeUnit: '1s', duration: DURATION, preAllocatedVUs: 10, maxVUs: 100, exec: 'search', startTime: '30s' },
    enroll: { executor: 'constant-arrival-rate', rate: ENROLL_RATE, timeUnit: '1s', duration: DURATION, preAllocatedVUs: 5, maxVUs: 50, exec: 'enrollNew', startTime: '30s' },
  },
  thresholds: {
    'http_req_duration{operation:verify}': ['p(95)<800'],
    'http_req_duration{operation:search}': ['p(95)<1500'],
    unexpected_failures: ['rate<0.01'],
  },
};

const headers = () => ({ 'X-Api-Key': API_KEY, 'Idempotency-Key': `${exec.scenario.name}-${exec.scenario.iterationInTest}-${Date.now()}-${Math.random()}` });

function record(res, operation) {
  if (res.status === 429) {
    limited.add(1);
    return;
  }
  const ok = check(res, { [`${operation} answered 200`]: (r) => r.status === 200 });
  unexpected.add(!ok);
}

export function enrolPool() {
  for (let i = 0; i < PEOPLE; i++) {
    const res = http.post(`${BASE_URL}/api/v1/faces/enroll`, {
      externalRef: `k6-person-${i}`,
      consentReference: 'load-test',
      image: http.file(enrolPhotos[i], 'photo.jpg', 'image/jpeg'),
    }, { headers: headers(), tags: { operation: 'enroll-setup' } });
    // 200 (enrolled) or 409 (the pool exists from an earlier run) are both fine.
    check(res, { 'pool enrolment accepted': (r) => r.status === 200 || r.status === 409 });
  }
}

export function verify() {
  const i = Math.floor(Math.random() * PEOPLE);
  const res = http.post(`${BASE_URL}/api/v1/faces/verify`, {
    externalRef: `k6-person-${i}`,
    image: http.file(probePhotos[i], 'probe.jpg', 'image/jpeg'),
  }, { headers: headers(), tags: { operation: 'verify' } });
  record(res, 'verify');
}

export function search() {
  const i = Math.floor(Math.random() * PEOPLE);
  const res = http.post(`${BASE_URL}/api/v1/faces/identify`, {
    image: http.file(probePhotos[i], 'probe.jpg', 'image/jpeg'),
  }, { headers: headers(), tags: { operation: 'search' } });
  record(res, 'identify');
}

export function enrollNew() {
  const n = exec.scenario.iterationInTest;
  const res = http.post(`${BASE_URL}/api/v1/faces/enroll`, {
    externalRef: `k6-new-${Date.now()}-${n}`,
    consentReference: 'load-test',
    image: http.file(enrolPhotos[n % PEOPLE], 'photo.jpg', 'image/jpeg'),
  }, { headers: headers(), tags: { operation: 'enroll' } });
  record(res, 'enroll');
}
