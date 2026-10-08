# Monitoring, alerting and log shipping

What exists in the repository (all under `deploy/observability/`), what you must provide, and the known gaps.

## 1. Health endpoints

| Endpoint | Meaning | Use for |
|---|---|---|
| API `GET /health/live` | Process is up and serving. Runs no checks. | Container/IIS liveness, load-balancer health, `docker compose` healthcheck. |
| API `GET /health/ready` | Database reachable (`database`) **and** tenant isolation intact (`tenant-protection`: RLS policy present, API login not privileged when `Database:RequireLeastPrivilege`). 503 when unhealthy. | Readiness gate for deployments and traffic; black-box probe alert. |
| Portal `GET /health/live` | Portal process up. The portal has no database; API reachability is surfaced to users, not probed here. | Liveness. |

All three are anonymous, exempt from the HTTPS redirect and skipped by tracing. `Host` must be an allowed host: probes sent by the proxy use `Host: localhost`, which the compose files allow explicitly (`AllowedHosts=<real-host>;localhost`).

## 2. Metrics and traces (OpenTelemetry)

Opt-in. Set `Telemetry__Enabled=true` and the standard variable `OTEL_EXPORTER_OTLP_ENDPOINT` (default protocol gRPC, e.g. `http://otel-collector:4317`) on the API and the portal. Instrumentation: ASP.NET Core server metrics/traces, outgoing HttpClient, .NET runtime metrics. Service names: `nexaverify-api`, `nexaverify-portal`. Request bodies, query strings and exception details are never attached to spans.

Local stack: `docker compose -f deploy/docker/docker-compose.dev.yml --env-file deploy/docker/.env --profile observability up` runs the OpenTelemetry Collector (`otel-collector.yaml`), Prometheus (`prometheus.yml` + `alerts.rules.yml`) and Grafana (dashboard provisioned from `grafana/dashboards/nexaverify-overview.json`; add a Loki data source for the log panels).

Key series (collector -> Prometheus naming): `http_server_request_duration_seconds_{bucket,count,sum}` with labels `service_name`, `http_route`, `http_request_method`, `http_response_status_code`; `kestrel_*`; `dotnet_*`.

## 3. Alert rules

`deploy/observability/alerts.rules.yml` (validated with `promtool check rules` in CI) and `loki-alerts.yml` (LogQL, for the Loki ruler or Grafana-managed alerts).

| Alert | Source | Threshold (starting point) | Runbook |
|---|---|---|---|
| NexaVerifyHealthProbeFailing | blackbox probe of `/health/live`, `/health/ready` | 3 min | [incident-response](incident-response.md) |
| ApiHighErrorRate / PortalHighErrorRate | HTTP 5xx ratio | 2 % API, 5 % portal for 10 min | incident-response |
| ApiHighLatencyP95 | all API routes | p95 > 1 s, 10 min | incident-response |
| FaceVerifyLatencyHigh / FaceIdentifyLatencyHigh | per-route p95 (`api/v1/faces/verify`, `/identify`) | 2 s / 3 s | incident-response |
| FaceImageGateShedding | 503 on face routes (decode queue full) | > 0.05 req/s | scale out / see CONFIG |
| LicenseExhaustionSpike | HTTP 402 (`LICENSE_INSUFFICIENT_BALANCE`) | > 50 / 15 min | contact clients; admin dashboard |
| AuthFailureSpike / AuthRateLimitHits / ApiKeyAuthFailureSpike | 401/403/429 on auth and face routes | 100 / 200 per 5 min | [emergency-revoke](emergency-revoke.md) |
| LedgerTamperingSuspected | log, event **7001** (Critical) | any | incident-response, section "Ledger integrity" |
| LedgerCheckpointsNotWritten | log, event **7002** absent | 36 h | incident-response |
| LedgerVerificationRunFailed, BackgroundJobFailures, ApiRequestLogDropping, EmailsBeingDropped, WebhookDeliveryBacklog | log | see file | incident-response |

Thresholds are starting points; tune them after the staging soak ([staging-soak-test](staging-soak-test.md)) and record the final values in the release checklist.

## 4. Logs

Containers write Serilog **compact JSON** to stdout (`Serilog:WriteTo` console + `CompactJsonFormatter`). Each event carries `@t`, `@mt` (message template), `@l` (level, absent for Information), `@x` (exception), `EventId` (`{"Id":7001,...}`), `Application`, `SourceContext` plus request properties (`RouteTemplate`, `ClientId`, `ActorId`, correlation id). Passwords, tokens, keys, image bytes and reset links are masked/never logged (`SensitiveDataDestructuringPolicy`, `Email:LogBodies` is refused in Production).

Ship them with any container log agent (Promtail/Alloy, Fluent Bit, Azure Monitor, CloudWatch). Label the API stream `app="nexaverify-api"` for the rules in `loki-alerts.yml`. IIS: in-process hosting has no container stdout. Add a rolling file sink through configuration (a `Serilog__WriteTo__1__Name=File` style override or an `appsettings.Production.json` next to the site) and have your agent tail that directory; the sink package must be added to the build if it is not already referenced (only the console sink ships today).

Retention (starting policy; set to your legal requirements): application logs 30 days hot / 90 days archive; security-relevant lines (event 7001/7002, auth failures) 1 year in write-once storage. The application purges its own API request log table after 90 days by default (see CONFIG) and finished webhook deliveries after 30 days; audit rows are append-only and not purged by the application.

### 4.1 Event 7002 (ledger checkpoints) must leave the database operator's reach

Every clean license verification writes an HMAC checkpoint to `licensing.LedgerCheckpoints` **and** logs it at Information with event id **7002**: `Ledger checkpoint written: license {LicenseId} entry {LastEntryId} rows {EntryCount} head {HeadHash} mac {Mac}`. Checkpoints in the database alone cannot catch an attacker who deletes the newest ledger rows together with the newest checkpoints; the logged copy can. Therefore:

1. Route lines matching `"Ledger checkpoint written"` (or `EventId.Id == 7002`) to a **separate, write-once / append-only destination** that database and application operators cannot edit (object storage with object lock, a SIEM index with restricted delete, WORM bucket). Keep at least the retention of the ledger (financial record: typically 7 years).
2. Alert if the stream is silent (`LedgerCheckpointsNotWritten`).
3. During an investigation compare the latest logged `rows`/`head` of a license to the live ledger (see incident-response, "Ledger integrity").

## 5. Known gaps (be explicit before go-live)

- **No business gauges.** The application does not yet emit metrics for webhook backlog (retrying/abandoned deliveries), license balances, ledger run status or queue depth. Today these are: portal admin dashboard (`GET /api/v1/admin/dashboard` carries `WebhookHealth` and expiring/low-balance lists) and the log-based alerts above. A follow-up should add a `System.Diagnostics.Metrics` meter (`NexaVerify`) with observable gauges and counters (webhook pending/abandoned, ledger break count, job last-success timestamps, image-gate queue depth) in `src/Infrastructure`; the collector, Prometheus and Grafana pipeline already carries any meter that is added with `AddMeter`.
- License exhaustion is detected through HTTP 402 volume, not through the number of exhausted licenses.
- Log field names used by the LogQL rules assume compact JSON and were derived from the live container output; verify them against your log pipeline's JSON parsing when you wire it (match is by literal message template to be robust).
- The shipped Grafana dashboard and rules are static-validated only (JSON parses, `promtool check rules`); they have not been run against real traffic.
