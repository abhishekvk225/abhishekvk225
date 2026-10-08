# Load tests

Two ways to load the face API (enroll, verify 1:1, search/identify 1:N). Both are black-box: they use an **API key** like an
integrator would. Neither belongs in the normal CI run against production-like data; run them against a dedicated environment.

| Tool | Use it for |
|---|---|
| `tests/Load/NexaVerify.Load` (.NET console) | Quick, dependency-free runs (also used by a smoke test in `Api.IntegrationTests`). Prints status codes and p50/p95/p99 per operation; exit code 1 when a budget is broken. |
| `tests/Load/k6/faces.js` ([k6](https://k6.io)) | Arrival-rate (open model) load with ramp/soak, thresholds and the usual k6 outputs (Grafana, Prometheus, JSON). |

## 1. Prepare a load tenant

The limiter and the licence are part of what you test, so give the load tenant room first (Super Admin, portal or API):

1. Create a client (for example code `LOAD`) and an active **license with plenty of credits**: each enroll/verify/identify costs the
   configured credits (default 1), so a 10-minute run at 30 req/s needs about 18 000.
2. Raise the client settings `api.rateLimitPerMinute` (max 100000) and `api.dailyQuota` above the planned load. Otherwise you
   measure the limiter: you will see `429 RATE_LIMITED` / `DAILY_QUOTA_EXCEEDED`, which both tools count separately from failures.
3. As the client admin create an **API key** with scopes `faces.enroll`, `faces.verify`, `faces.identify` and a per-key rate limit at
   (or above) the account limit. Keep the raw key in `NV_API_KEY`.
4. Use a copy of production data volumes if you want identify (1:N) numbers that mean something: the template index is held in
   memory per client, so p95 grows with the number of enrolled people. The target is p95 <= 1.5 s over 10 000 templates (docs/01 section 10).

`FaceEngine:Provider=mock` (the only provider shipped today) is deterministic and cheap: it proves the platform (limiter, licence
transaction, database, image gate), not a real model. Re-run with the real provider when it lands; the scripts do not change but the
fixtures must then be real photos.

## 2. .NET driver

```bash
export NV_BASE_URL=https://api.staging.example
export NV_API_KEY=...            # the load tenant's key

dotnet run -c Release --project tests/Load/NexaVerify.Load -- run \
  --concurrency 8 --duration 120 --profiles 50 --verify 70 --identify 20 --enroll 10 \
  --max-error-rate 0.01 --verify-p95-ms 800 --identify-p95-ms 1500
```

It first enrolls `--profiles` people, then mixes the operations over `--concurrency` workers for `--duration` seconds.
Output: requests/s, status-code histogram, p50/p95/p99 per operation. `429` is reported as `rate-limited`, not as an error.
Exit code: `0` all budgets met, `1` a budget was broken, `2` bad arguments or setup failure (for example the key lacks a scope).

## 3. k6

```bash
dotnet run --project tests/Load/NexaVerify.Load -- fixtures --out tests/Load/k6/fixtures --count 20   # synthetic JPEGs for the mock engine
k6 run -e BASE_URL=$NV_BASE_URL -e API_KEY=$NV_API_KEY \
       -e VERIFY_RATE=30 -e SEARCH_RATE=10 -e ENROLL_RATE=2 -e DURATION=5m tests/Load/k6/faces.js
```

Thresholds (the run fails when broken): verify p95 < 800 ms, identify p95 < 1500 ms, unexpected failures < 1 %.
The `setup_people` scenario enrolls the pool first (`409` for people from an earlier run is fine). Fixtures are not committed
(`tests/Load/k6/fixtures/` is git-ignored); use real photos for a real provider.

## 4. What to watch while it runs

* API: `/health/ready`, CPU, the face-engine concurrency queue (a `503` with `Retry-After` means load shedding kicked in, which is
  working as designed but means you are above the node's capacity), request-log p95 on the client dashboard.
* SQL Server: `api.UsageCounters` row count stays bounded (the purger keeps one hour of minute buckets); the licence `UPDATE`
  stays a single-row statement (watch for lock waits on `licensing.Licenses`).
* Several API nodes: run the same test through the load balancer with `Counters:Shared=true` and confirm the per-key limit holds in
  total (not per node): 429s start when the *sum* reaches the limit.

## 5. Interpreting results

| Symptom | Likely cause |
|---|---|
| Many `429` right away | Load tenant limits too low (step 1.2), or the key's own rate limit. |
| `402`/`LICENSE_INSUFFICIENT_BALANCE` mid-run | Not enough credits for the planned duration. |
| `503` with `Retry-After` | Face-engine / image-gate queue is full: node saturated; add nodes or raise the engine concurrency. |
| Verify fast, identify slow and growing | Template index size (1:N is linear in enrolled people); consider a vector index behind `ITemplateIndex`. |
| Latency spikes every ~2 s | Webhook dispatcher / log writer sharing the node; move background work to a worker node. |
