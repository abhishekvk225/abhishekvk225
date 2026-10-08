# Staging soak test plan

Purpose: prove, on an environment shaped like production, that a release candidate stays healthy under sustained realistic load, survives the failures we expect, and that operators can deploy, roll back, restore and be alerted. Output: the filled-in results table at the end, attached to the release checklist. Nothing here has been executed yet (it needs a real staging environment).

## 1. Preconditions

- Release candidate tag (`vX.Y.Z-rc.N`) deployed to staging by the pipeline ([deploy](deploy.md)); `scripts/smoke-test.sh` green.
- Staging mirrors production: same images, same proxy/TLS, **real** SQL Server edition and sizing class, `Production` environment (not Development), real `ForwardedHeaders`, OpenTelemetry on, logs shipped (including event 7002 to the external store), alert rules loaded, dashboards visible.
- Face engine: state which one runs. The shipped `mock` engine recognises nothing; load results with it say nothing about a real engine's CPU cost. If the real provider is not available, the soak validates everything except recognition throughput and the release notes must say so.
- Seed data: 20 clients, 5 000 face profiles each (100 000 total), 2 API keys per client, licenses with different balances/expiry (include near-empty, expired and suspended ones), 3 webhook endpoints per client (one failing on purpose).
- Test users per portal role; MFA enrolled for staff.
- Load generator on a separate host in the same region: k6 (script skeleton below) or equivalent.

## 2. Scenarios

| # | Scenario | Duration | Load | Observe |
|---|---|---|---|---|
| S1 | Baseline | 1 h | 10 req/s mixed: 60 % verify, 20 % identify, 10 % enroll/delete, 10 % portal/API reads | latency p50/p95/p99 per route, 5xx, DB CPU/IO, API CPU/memory |
| S2 | Soak | **24 h** (48 h before a major release) | 30 req/s steady, diurnal wave x2 | memory growth (flat after warm-up), GC pauses, connection counts, log volume, queue depth, thread pool, disk |
| S3 | Peak | 30 min | 3x the S2 rate, then 5x for 5 min | 503 load-shedding behaviour (must shed, not crash), recovery time, rate-limit 429s |
| S4 | Noisy neighbour | 30 min | one client at 10x quota, others normal | other clients' p95 unchanged, daily quota/rate limits enforced, kill switch works under load |
| S5 | Licensing under concurrency | 30 min | 200 parallel charges against a 1 000-credit license | balance never negative, exact credits consumed, nightly ledger verification clean afterwards |
| S6 | Background jobs | during S2 | - | ledger verification runs (event 7002 appears, exclusive across nodes), alert job de-dupes, retention sweeper drains, webhook retries/abandonment with the failing endpoint, no job errors |
| S7 | Failure: API node restart | 15 min | S1 load | in-flight failures only for the killed node, health-based removal, sessions re-established (portal users sign in again) |
| S8 | Failure: SQL Server restart / failover | 15 min | S1 load | readiness 503 within seconds, no process crash, recovery without restart, no duplicated charges (idempotency keys), alerts fire |
| S9 | Failure: dependency egress blocked (webhooks) | 15 min | S1 | webhook retry/backoff, no impact on API latency |
| S10 | Deploy under load | 1 run | S1 | rolling restart: error budget, zero failed health transitions beyond the window |
| S11 | Rollback under load | 1 run | S1 | [rollback](rollback.md) to previous tag, time to healthy, smoke test |
| S12 | Backup / restore drill | 1 run | none | restore the last backup to a scratch instance: `RESTORE VERIFYONLY`, restore, app starts against it, ledger verification clean; record **RTO/RPO** |
| S13 | Alert drill | 1 h | - | induce each alert (stop a container, send bad logins, exhaust a test license, make verification fail on a test license by editing a copy of the data, mute 7002 shipping) and confirm the page/ticket arrives with a working runbook link |
| S14 | Security regression | 1 run | - | smoke test, TLS scan (no TLS < 1.2), header check, auth negative tests, cross-tenant probe with two clients' keys, `AllowedHosts` with a foreign Host, upload limit (> 6 MiB rejected at proxy/IIS), rate-limit behaviour with real client IPs |
| S15 | Emergency drill | 1 run | S1 | [emergency-revoke](emergency-revoke.md): revoke one key, kill switch on/off, propagation <= `ApiAuth:CacheSeconds` + 5 s on every node |
| S16 | Key rotation rehearsal | 1 run | none | JWT key rotation per [key-rotation](key-rotation.md); master-key rotation **only if the rotation tool exists** (otherwise record it as a blocker/accepted risk) |

## 3. k6 skeleton (adapt, unverified)

```javascript
// scripts/soak/k6-soak.js (create when needed): run with  k6 run -e API=https://api.staging.example -e KEY=nv_xxx -e PHOTO=@face.jpg soak.js
import http from 'k6/http';
import { check, sleep } from 'k6';
export const options = { scenarios: { steady: { executor: 'constant-arrival-rate', rate: 30, timeUnit: '1s', duration: '24h', preAllocatedVUs: 100, maxVUs: 400 } },
  thresholds: { http_req_failed: ['rate<0.01'], 'http_req_duration{route:verify}': ['p(95)<2000'] } };
const photo = open(__ENV.PHOTO, 'b');
export default function () {
  const res = http.post(`${__ENV.API}/api/v1/faces/verify`, { image: http.file(photo, 'face.jpg', 'image/jpeg'), externalRef: 'soak-person-1' },
    { headers: { 'X-Api-Key': __ENV.KEY, 'Idempotency-Key': `${__VU}-${__ITER}` }, tags: { route: 'verify' } });
  check(res, { 'status ok': r => [200, 402, 429].includes(r.status) });
  sleep(0.1);
}
```

Field and header names follow `docs/03-api-specification.md` (`image`, `externalRef`, `X-Api-Key`, `Idempotency-Key`); the snippet is a shape, not a tested script.

## 4. Pass criteria (starting values; tighten with the first run)

- Availability during S1-S6: >= 99.9 % of requests non-5xx; zero crashes/restarts not caused by the scenario.
- Latency at S2 steady state (real engine): verify p95 <= 2 s, identify p95 <= 3 s, other API routes p95 <= 300 ms.
- Memory: no upward trend after the first hour of S2 (< 5 % growth over 12 h); no handle/thread leaks.
- Zero cross-tenant data exposure, zero negative balances, ledger verification clean after S2/S5, event 7002 present every 24 h.
- Every scenario's expected alert fired; no unexplained alert.
- S12: restore succeeded; RTO <= 4 h and RPO <= 15 min (or the agreed numbers).
- No Critical/High finding open from CI (Trivy, vulnerable packages, CodeQL, gitleaks).

## 5. Results (fill in)

| Scenario | Date | Result (pass/fail) | Key numbers | Link to dashboard/logs | Follow-up |
|---|---|---|---|---|---|
| S1 | | | | | |
| ... | | | | | |

Sign-off: Engineering ____ Operations ____ Security ____ Date ____
