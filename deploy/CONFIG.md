# Configuration catalogue

Layering: `appsettings.json` (safe defaults, **no secrets**) → `appsettings.{Environment}.json` → environment variables
(`Section__Key`) / secret store → command line. Required values fail fast at startup. **Never commit secrets.**

## Required in every environment
| Key | Purpose | Secret | Notes |
|---|---|---|---|
| `ConnectionStrings:Default` | SQL Server connection | **yes** | API: the *least-privilege* login created by `Migrator app-principal`. Migrator: an administrative login. Production must use `Encrypt=True` with a trusted certificate (`TrustServerCertificate=True` is local-dev only). |
| `Jwt:SigningKeyPem` | ECDSA P-256 private key (PEM) that signs access tokens | **yes** | Production refuses `Jwt:AllowEphemeralKey`. Rotate by changing `Jwt:SigningKeyId` and listing the old public key under `Jwt:AdditionalValidationKeys`. |
| `Encryption:MasterKeyBase64` | 32-byte master key (KEK) that wraps every client's data key | **yes** | Production refuses `Encryption:AllowEphemeralKey`. Losing this key makes client biometric data unrecoverable; back it up in a vault. |
| `AllowedHosts` | Accepted `Host` header values | no | `*` is rejected in Production. |
| `Auth:PasswordResetUrlTemplate` | Link in reset/invitation emails (`{email}`, `{token}`) | no | Must be https (http only for localhost). Never built from request headers. |

## Security & hosting
| Key | Default | Notes |
|---|---|---|
| `Hosting:RedirectToHttps` | `true` (`false` in Development) | Plain HTTP → 308 to HTTPS (`/health/*` exempt). Behind a TLS-terminating proxy configure `ForwardedHeaders`, otherwise every request looks like HTTP. |
| `ForwardedHeaders:Enabled/KnownProxies/KnownNetworks/ForwardLimit` | disabled | Required behind a proxy; startup fails if enabled without a trust list; `/0` networks are rejected. Needed for correct client IPs (rate limits, lockout history, allow-lists). |
| `SecurityHeaders:*` | strict API defaults | `HstsMaxAgeSeconds=0` disables HSTS. |
| `Cors:AllowedOrigins` | `[]` | Explicit `https://host[:port]` origins only. |
| `RequestLimits:*` | 6 MiB body, 30 s headers, 32 KiB header size, 240 B/s minimum rate, 60 s request timeout, JSON depth 32 | |
| `RateLimiting:PerIpPerMinute` / `AuthPerIpPerMinute` | 300 / 10 | Per client IP (IPv6 per /64); auth limit is per endpoint. |
| `Database:RequireLeastPrivilege` | `false` (`true` in Production) | Readiness fails if the API's DB login is sysadmin/db_owner/can alter security policies. |
| `Database:CommandTimeoutSeconds` | 30 | |

## Face recognition
| Key | Default | Notes |
|---|---|---|
| `FaceEngine:Provider` | `mock` | Only `mock` ships today. The mock engine recognises nothing (thumbnail comparison), so **Production refuses to start with it** unless `FaceEngine:AllowMockInProduction=true` (demo deployments only). A real provider (ONNX/ArcFace, cloud) plugs in behind `IFaceEngine`. |
| Image rules | fixed | JPEG/PNG/WebP by magic bytes, ≤ 5 MB, ≤ 25 megapixels, ≥ 64 px, re-encoded as JPEG (metadata stripped), at most 4 images decoded concurrently per node. |
| Per-client settings | catalogue | `face.matchThreshold`, `face.maxFacesPerImage`, `face.minQuality`, `face.retentionDays`, `face.identifyTopK`, `limits.maxProfiles` (see the settings API). Retention is enforced hourly by a background sweeper. |

## Identity
| Key | Default | Notes |
|---|---|---|
| `Auth:MaxFailedAttempts` / `LockoutMinutes` | 5 / 15 | Atomic, per account. Lockout blocks password sign-in only (not live sessions, not password reset). |
| `Auth:RefreshTokenSlidingDays` / `AbsoluteDays` | 7 / 30 | |
| `Auth:RefreshReuseGraceSeconds` | 10 | A rotated token replayed within this window is refused but not treated as theft. |
| `Auth:PasswordResetMinutes` / `InvitationHours` / `PasswordResetCooldownSeconds` | 30 / 72 / 60 | |
| `Auth:SensitiveResponseMinimumMilliseconds` | 300 | Forgot/reset-password responses take at least this long (no timing oracle). |
| `PasswordPolicy:MinLength/MaxLength` | 12 / 128 | |
| `PasswordHashing:IterationCount` | 210000 | PBKDF2-HMAC-SHA512; stronger values upgrade hashes at next sign-in. |
| `Jwt:Issuer/Audience/AccessTokenMinutes/ClockSkewSeconds` | `nexaverify` / `nexaverify-api` / 15 / 60 | |
| `Seed:SuperAdminEmail/SuperAdminPassword/SuperAdminName` | unset | Migrator only. Creates the first Super Admin once (must change password at first sign-in). `Migrator recover-superadmin` is the break-glass path. |

## Two-factor authentication (M9a)
| Key | Default | Notes |
|---|---|---|
| `Mfa:Enabled` | `true` | Master switch for enrolment, the second sign-in step and enforcement. Accounts that already enrolled are **always** challenged, whatever this says. |
| `Mfa:RequiredPlatformRoles` | `SuperAdmin` | Comma-separated platform roles that **must** enrol (`security.requireMfa` for staff). Such an account gets an enrolment-only token (`mer` claim, `mfaEnrolmentRequired: true`) until it has a second factor; the same pattern as a forced password change. Use `-` for "nobody" (Development and the test host do; see below). |
| `Mfa:AllowClientUsers` | `true` | Client users may enrol; each client turns on the requirement for all its users with its own `security.requireMfa` setting. |
| `Mfa:Issuer` | `NexaVerify` | Label shown in the authenticator app. |
| `Mfa:ChallengeMinutes` / `MaxChallengeAttempts` | 5 / 5 | Lifetime of the ticket between "password accepted" and "second factor proven" (single use) and the codes it accepts, right or wrong. Failed codes also count against the account's `Auth:MaxFailedAttempts` lockout. |
| `Mfa:Window` | 1 | Accepted clock drift in 30-second steps either side of now (TOTP RFC 6238: SHA-1, 30 s, 6 digits). An accepted step can never be reused (replay protection). |
| `Mfa:RecoveryCodeCount` | 10 | Single-use recovery codes issued at enrolment and on regeneration (shown once, stored as SHA-256). |

TOTP secrets are encrypted at rest (AES-256-GCM, key derived from `Encryption:MasterKeyBase64` with HKDF, purpose `mfa-secret`, authenticated with the user id). **The master key therefore protects MFA as well**: with a throw-away development key (`Encryption:AllowEphemeralKey`) enrolled secrets become unreadable after a restart, which is why `appsettings.Development.json` sets `Mfa:RequiredPlatformRoles` to `-`. A lost authenticator is fixed by another Super Admin (`POST /api/v1/admin/users/{id}/mfa/reset`, reason required, audited); the very last Super Admin recovers with `Migrator recover-superadmin` plus a database-side reset of `iam.UserMfa`. The portal needs no extra settings: the API's challenge is parked in the portal's session cache (cookie `__Host-nv.mfa`, opaque, HttpOnly, lifetime = the challenge).

## Licensing controls (M9a)
| Key | Default | Notes |
|---|---|---|
| `Licensing:MaxAdjustPerAction` | 10000 | Largest credit adjustment (up **or** down) one person may apply. Above it `POST /admin/licenses/{id}/adjust` files a request (202) that a different user holding `licenses.approve-adjust` must approve. Not editable through the API on purpose (a compromised admin cannot raise their own limit). |
| `Licensing:AdjustApprovalHours` | 24 | How long an approver has; afterwards the request is `Expired` and must be raised again. |

## API access control (M9a)
| Key | Default | Notes |
|---|---|---|
| `ApiAuth:CacheSeconds` | 5 | How long a node trusts its cached view of an API key (status, scopes, the client's IP allow-list, the client kill switch). **Propagation bound**: a revoked key / the kill switch (`PUT /admin/clients/{id}/api-access`) takes effect on the node that handled the call immediately and on every other node within this many seconds. Lower = faster, more database reads. |
| `ApiAuth:NegativeCacheEntries` / `NegativeCacheSeconds` | 5000 / 5 | Size and age of the bounded cache of unknown key prefixes (random probing cannot grow memory). |
| `Faces:Retention:Enabled` | `true` | Hourly sweep: erases expired face profiles (drained batch by batch) and blanks personal data on old recognition history. |
| `Faces:Retention:HistoryPersonalDataDays` | 90 | After this many days a recognition-history row loses its image fingerprint and IP address (outcome, score, cost stay for billing). |
| `Faces:Retention:BatchSize` / `MaxBatchesPerClient` | 200 / 500 | Batch size and a safety valve per client and run. |

## Usage, dashboards and alerts (M7)
| Key | Default | Notes |
|---|---|---|
| `Dashboards:DefaultDays` / `MaxDays` | 30 / 90 | Window of the client and admin dashboards; `days` outside 1..`MaxDays` is rejected with 400. |
| `Dashboards:TopApiKeys` / `TopClients` / `AttentionListSize` | 5 / 10 / 10 | Rows in the "top" lists and the expiring / low-balance lists (their counts are always complete). |
| `Dashboards:ExpiringWithinDays` / `LowBalancePercent` | 30 / 10 | Admin "expiring" horizon and the "low balance" threshold (share of credits left). |
| `Dashboards:LatencyBucketMilliseconds` / `LatencyCapMilliseconds` | 25 / 10000 | The p95 latency is read off a histogram of this resolution (exact to one bucket); slower requests count in the last bucket. |
| `Dashboards:DefaultReportDays` / `MaxReportDays` | 30 / 92 | Default and maximum range of one usage CSV export. |
| `Metering:LedgerVerification:Enabled` | `true` | Nightly tamper check of every license ledger (hash chain, balance **and the signed checkpoints**). A break is announced **once**: Critical log (event id 7001: alert on it) and audit entry `ledger.verification_failed` when first found, then only a reminder every `ReminderDays`; the nights in between log a Warning (7003, "still unresolved"). Open findings are listed by `GET /api/v1/admin/licensing/ledger-breaks` (the operator-visible list: `alertedAt`, `lastReminderAt`, `timesSeen`). The on-demand `POST /api/v1/admin/licensing/verify-ledger` (202 + run id, poll `GET .../runs/{id}`) works even when this is off. |
| *(ledger anchors)* | | Each license that verifies clean gets an HMAC-SHA256 checkpoint (`licensing.LedgerCheckpoints`, append-only) keyed from `Encryption:MasterKeyBase64` (purpose `ledger-anchor`, never stored in the database). **Changing the master key invalidates all checkpoints** (reported as failed signatures): re-anchor deliberately after a rotation. Every checkpoint is also logged at Information (event 7002, head hash and MAC): ship that log line to storage the database operator cannot edit, because an attacker who can delete the newest checkpoints together with the newest ledger rows is only caught by such an external copy. |
| `Metering:LedgerVerification:IntervalHours` / `InitialDelayMinutes` | 24 / 10 | Run cadence and the wait after start-up (so a restart does not skip the check). |
| `Metering:LedgerVerification:LicenseBatchSize` / `EntryBatchSize` | 200 / 1000 | Paging of the read-only scan. |
| `Metering:LedgerVerification:ReminderDays` | 7 | A break that stays unresolved is re-announced (Critical 7001) every this many days; `0` = alert once, never remind. |
| `Metering:LedgerVerification:BalanceRecheckAttempts` / `RecheckDelayMilliseconds` | 2 / 250 | A balance-only mismatch is re-checked (a charge may have landed mid-scan) before being reported; a broken row is reported at once. |
| `Metering:Alerts:Enabled` | `true` | Hourly job raising `license.low_balance`, `license.exhausted`, `license.expiring`, `license.expired` and `apikey.expiring` (webhook + in-app notification). Set `false` in tests. |
| `Metering:Alerts:IntervalMinutes` / `InitialDelaySeconds` | 60 / 60 | |
| `Metering:Alerts:LowBalancePercent` | 10 | Credits left (or less) at which the low-balance alert fires. |
| `Metering:Alerts:ExpiringNoticeDays` / `ExpiringFinalNoticeDays` | 7 / 1 | Days before the end date of a license of the two expiry notices (final must be shorter). |
| `Metering:Alerts:ApiKeyExpiringDays` | 7 | |
| `Metering:Alerts:ExpiredLookbackDays` | 3 | A license that ended longer ago is not announced (no history dump on first run). |
| `Metering:Alerts:BatchSize` | 500 | Page size of the candidate scan. |

Alerts are de-duplicated by a unique `(subject, type, bucket)` row (`licensing.LicenseAlerts`), so restarts and several API nodes cannot send one twice; a renewal or top-up changes the bucket so the next crossing alerts again. The job runs on every node that hosts the API; split it onto a worker later without code change.

## Blazor portal (`src/Web/Blazor`, backend-for-frontend)
The portal is a separate deployable. The browser only ever holds an opaque, HttpOnly, `SameSite=Strict`, `Secure` session cookie; the JWT and refresh token stay on the portal server (docs/ui-notes.md, "M8a: BFF and session design").
| Key | Default | Notes |
|---|---|---|
| `Api:BaseUrl` | *(required; `https://localhost:7101` in Development)* | Address of the NexaVerify API (with or without a trailing `/api/v1`). Must be absolute **https** outside Development/Testing/UiDemo and must not carry credentials; the portal refuses to start otherwise. |
| `Api:TimeoutSeconds` | 30 | Per call to the API (enforced per call; the HttpClient itself has no timeout). A timeout is shown as "taking longer than expected", an unreachable API as "can't reach the service"; never a raw exception. |
| `Api:LongRunningTimeoutSeconds` | 300 | For operations that can legitimately run long (the on-demand credit ledger scan). After a timeout the Reports page holds the button back for 3 minutes so runs do not stack. |
| `Session:CookieName` | *(empty)* | Empty = `__Host-nv.session` when cookies are Secure (browsers then refuse a cookie planted from a sibling subdomain; antiforgery uses `__Host-nv.af`) and `nv.session` for plain-http Development. A `__Host-` name with `RequireSecure=false` is refused at startup. |
| `Session:AllowInMemoryStore` | `false` | Sessions live in this process's memory (lost on restart, not shared between nodes). Outside Development/Testing the portal refuses to start unless you set this to `true` on purpose (single node or sticky sessions) **or** configure a shared store (`PortalCache:Provider`, below). |
| `Session:IdleTimeoutMinutes` | 30 | Sliding window: no portal activity for this long ends the session. An open circuit notices within 30 s. |
| `Session:AbsoluteTimeoutHours` | 12 | Hard limit from sign-in, however active the user is. Keep it at or below the refresh-token lifetime (`Auth:RefreshTokenDays`). |
| `Session:RefreshSkewSeconds` | 30 | Access tokens are refreshed this many seconds before they expire. |
| `Security:Cookies:RequireSecure` | `true` (`false` in Development) | Session and antiforgery cookies are `Secure`-only. |
| `Security:Cookies:SameSite` | `Strict` | |
| `Security:Headers:*` | see appsettings | CSP with per-request nonce, Permissions-Policy etc. (unchanged). |
| `DataProtection:KeyPath` | *(required outside Development/Testing)* | Directory for the Data Protection key ring that encrypts session payloads, the cookie ticket and antiforgery tokens: shared, persistent and access-restricted (OS permissions or a mounted secret volume; keys are plain XML on disk, so whoever can read the directory and the cache can read sessions). |
| `ForwardedHeaders:Enabled/KnownProxies/KnownNetworks/ForwardLimit` | disabled | Same rules as the API: required behind a TLS-terminating proxy; startup fails without a trust list, with a `/0` network, with an unparsable entry or a limit outside 1-10. The portal forwards the person's address to the API (`X-Forwarded-For`) on sign-in, refresh, password reset and every circuit call. |
| *(fixed, not configurable)* | | The portal's CSV download relay (`/bff/reports/usage.csv`, `/bff/client/reports/usage.csv`) allows 6 exports per minute per session and only same-origin requests. The SignalR hub keeps its default 32 KB message limit; camera photos are streamed in chunks (max 5 MB). |
| `Ui:UseStubClients` | `false` | Design-review stubs that accept **any password**. Allowed in Development, or in a `UiDemo` host that also sets `Ui:AllowDemoStubs=true`; refused everywhere else. |
| `Ui:AllowDemoStubs` | `false` | Second key required for stubs in `UiDemo`. `UiDemo` gets no other relaxation: it must satisfy every rule below. |

**Startup guards.** Only `Development` is relaxed. Any other environment name (Production, Staging, `UiDemo`, anything) refuses to start with: `Security:Cookies:RequireSecure=false`, `Security:Cookies:SameSite` other than Strict, `Security:Headers:Enabled`/`ContentSecurityPolicyEnabled` off or `AllowInsecureWebSockets` on, `AllowedHosts` empty or `*`, a non-https `Api:BaseUrl` (`Testing` may use http), invalid numbers (`Api:*`, `Session:*`, idle timeout above the absolute timeout). Outside `Development` and `Testing` it also needs `DataProtection:KeyPath` and `Session:AllowInMemoryStore=true`.

Multi-node notes: the session store is `IDistributedCache` (in-memory by default = single node or sticky sessions). For several portal nodes set `PortalCache:Provider` to `SqlServer` or `Redis` (see "Scalability and multi-node operation") and share `DataProtection:KeyPath`; the single-flight token refresh is per process, so keep a session on one node (sticky) or accept that two nodes refreshing at the same instant look like token reuse to the API and end that session.
On the API side: add the portal's address to `ForwardedHeaders:KnownProxies` so the end user's address (sent as `X-Forwarded-For` on sign-in) drives the per-IP auth limits, otherwise all sign-ins share the portal's IP bucket. Set `Auth:PasswordResetUrlTemplate` to the portal's `/reset-password?email={email}&token={token}`.

## Secrets from files, observability and release plumbing (M10)
| Key | Default | Secret | Notes |
|---|---|---|---|
| `NEXAVERIFY_SECRETS_DIR` | `/run/secrets` | no (points at secrets) | API, portal and migrator read **every file in this directory as a setting**: file name = setting name with `__` for `:` (`Jwt__SigningKeyPem`, `ConnectionStrings__Default`, `Encryption__MasterKeyBase64`, `Seed__SuperAdminPassword`, `Migrator__AppPassword`). Applied after environment variables, so a mounted secret wins. A missing directory is fine. Compose secrets use `target:` to set the file name; on IIS point it at an ACL-restricted folder (docs/runbooks/iis-deployment.md). A trailing newline is trimmed. |
| `Telemetry:Enabled` | `false` | no | API and portal. Turns on OpenTelemetry metrics and traces (ASP.NET Core, HttpClient, .NET runtime) exported over OTLP. Query strings and exception details are not recorded; `/health/*` is not traced. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | no | Standard OpenTelemetry variable, e.g. `http://otel-collector:4317`. `OTEL_EXPORTER_OTLP_HEADERS` (hosted backends) **is a secret**: use your platform's secret store. |
| `Serilog:*` | console, compact JSON | no | Sinks/levels via `Serilog__MinimumLevel__Default` etc. Only the console sink ships (docs/runbooks/monitoring.md covers shipping and event 7002). |
| `ApiLogs:RetentionDays` | 90 | no | Retention of `api.ApiRequestLogs` (purged by the API). |
| Portal `GET /health/live` | - | - | Liveness of the portal (anonymous, exempt from the HTTPS redirect). The API exposes `/health/live` and `/health/ready`. |

### Compose and pipeline variables (not application settings)
| Name | Where | Purpose |
|---|---|---|
| `MSSQL_SA_PASSWORD`, `APP_DB_PASSWORD`, `SEED_ADMIN_EMAIL`, `SEED_ADMIN_PASSWORD`, `TELEMETRY_ENABLED`, `GRAFANA_ADMIN_PASSWORD` | `deploy/docker/.env` (dev compose; copy `.env.example`; never committed) | Local development only. |
| `NEXAVERIFY_REGISTRY`, `NEXAVERIFY_TAG`, `API_HOST`, `PORTAL_HOST`, `SEED_ADMIN_EMAIL`, `MSSQL_PID`, `ALLOW_MOCK_FACE_ENGINE`, `TELEMETRY_ENABLED` | `deploy/docker/.env.prod` (production-like compose; copy `.env.prod.example`; never committed) | Non-secret deployment settings. |
| `deploy/docker/secrets/*` | host directory generated by `deploy/docker/prod/make-secrets.sh` (never committed) | `sa_password`, `app_db_password`, `seed_admin_password`, `migrator_connection`, `api_connection`, `jwt_signing_key.pem`, `master_key`, mounted as Compose secrets. |
| `DEPLOY_SSH_KEY`, `DEPLOY_SSH_KNOWN_HOSTS` | GitHub environment **secrets** (`staging`, `production`) | Deploy-only SSH identity and pinned host key. |
| `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_DIR`, `API_URL`, `PORTAL_URL` | GitHub environment **variables** | Target host, remote directory (default `/opt/nexaverify`), URLs for the smoke test. |
| `SMOKE_INSECURE`, `SMOKE_RESOLVE`, `SMOKE_RETRIES`, `SMOKE_DELAY` | `scripts/smoke-test.sh` | Rehearsal switches (self-signed certificate, host mapping, warm-up). |

### Production-like compose: what is set where
| Setting | Value / source |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `AllowedHosts` | `<API_HOST or PORTAL_HOST>;localhost` (`localhost` lets the container health probe through) |
| `ConnectionStrings__Default`, `Jwt__SigningKeyPem`, `Encryption__MasterKeyBase64` | Compose secrets (files) |
| `ForwardedHeaders__Enabled`, `__KnownNetworks__0`, `__ForwardLimit` | `true`, `172.29.0.0/24` (the proxy subnet), `1` |
| `Auth__PasswordResetUrlTemplate`, `Cors__AllowedOrigins__0` | derived from `PORTAL_HOST` |
| `FaceEngine__AllowMockInProduction` | `ALLOW_MOCK_FACE_ENGINE` (demo / rehearsal only) |
| Portal `Api__BaseUrl`, `DataProtection__KeyPath`, `Session__AllowInMemoryStore` | `https://<API_HOST>`, `/keys` (named volume), `true` |
## Scalability and multi-node operation (M9b)
Several API nodes behind a load balancer need no sticky sessions. What is shared, how, and what to set:

| Key | Default | Notes |
|---|---|---|
| `Counters:Shared` | `true` | Per-credential per-minute limit and per-client daily quota (and the per-principal throttles below) are counted in SQL Server (`api.UsageCounters`), so the limits hold **across nodes**. `false` = every node counts alone (limits multiply by the node count; fine for one node and tests). If the database is unreachable a node falls back to counting alone and logs a warning once a minute. |
| `Counters:ReservationDivisor` / `MaxReservation` | 20 / 50 | A node reserves up to `limit / divisor` permits (1 .. max) from the database at a time and serves them from memory, so roughly one call in that many touches SQL. The sum over all nodes never exceeds the limit; permits a quiet node reserved are lost for that window (small limits therefore reserve one at a time and are exact). |
| `Counters:ExhaustedRecheckSeconds` | 2 | After the database refused a permit, further calls for that key are refused from memory for this long. |
| `Counters:ShortBucketRetentionMinutes` / `DailyBucketRetentionDays` / `PurgeIntervalMinutes` | 60 / 3 / 10 | Old buckets are purged in batches by every node (idempotent). |
| `Throttle:DashboardsPerMinute` | 60 | Per signed-in user / API key, shared across nodes. Over the limit: `429 RATE_LIMITED` + `Retry-After`. |
| `Throttle:ExportsPerMinute` | 6 | CSV exports (admin and client). The portal's own per-session limit (also 6) sits in front of it. |
| `Throttle:WebhookTestsPerMinute` / `WebhookRetriesPerMinute` | 10 / 30 | "Send test event" and manual delivery retry. |
| `Webhooks:BatchSize` | 50 | Deliveries one dispatcher cycle (every 2 s) claims across all endpoints. |
| `Webhooks:MaxPerEndpointPerCycle` | 5 | **Fairness/throttle**: one endpoint can take at most this many of a cycle's deliveries, oldest first, so a huge backlog or a slow receiver cannot starve other clients. Also the maximum send rate per endpoint (this many per 2 s per node). |
| `Webhooks:Parallelism` | 8 | Sends in flight per node. |
| `Webhooks:LeaseSeconds` | 0 | How long a claimed delivery is reserved for its node before another node may take it (crash recovery). `0` = derived from the worst case of one cycle: `ceil(BatchSize / Parallelism) x (TimeoutSeconds + 5) + 30`. A smaller explicit value is **refused at start-up** (a delivery still in flight could be sent twice). |
| `Webhooks:TimeoutSeconds` | 5 | 1..30, per delivery. |
| `Webhooks:DisableAfterFailedEvents` | 20 | An endpoint is switched off after this many **consecutive failed events**. An event counts once, when its first attempt fails (its up to 8 retries do not pile on); any success resets the count; test events never count. |

`api.UsageCounters` holds one small row per active credential per minute and per client per day; it stays bounded by the purger.

### Portal (BFF) shared store
The portal keeps sessions, pending MFA sign-ins and (softly) its export throttle in an `IDistributedCache`. Choose where:

| Key | Default | Notes |
|---|---|---|
| `PortalCache:Provider` | `Memory` | `Memory` (this process: single node or sticky sessions; outside Development/Testing it needs `Session:AllowInMemoryStore=true`), `SqlServer` or `Redis`. With a shared provider the in-memory opt-in is not needed, and a sign-in started on one node (password step, MFA challenge) can finish on another. |
| `PortalCache:SqlServer:ConnectionString` | | **Secret.** Database holding the cache table; preferably a small database of its own, with a login that can only use that table. |
| `PortalCache:SqlServer:SchemaName` / `TableName` | `dbo` / `PortalCache` | Plain identifiers only. Create the table with `deploy/sql/portal-cache.sql` (same shape as `dotnet sql-cache create`), or set `EnsureTable=true` to let the portal create it at start-up (needs DDL rights; leave off with a least-privilege login). |
| `PortalCache:SqlServer:ExpiredItemsDeletionIntervalMinutes` | 30 | |
| `PortalCache:Redis:Configuration` / `InstanceName` | | **Secret** (may contain a password). StackExchange.Redis configuration string; `InstanceName` prefixes the keys (default `nexaverify-portal:`). |

Everything stored is encrypted with ASP.NET Data Protection **before** it reaches the cache (a cache dump shows no tokens), so every portal node must share the **same key ring**: mount one `DataProtection:KeyPath`. Known limits with several nodes: the single-flight token refresh is per process (two nodes refreshing the same session at the same instant look like refresh-token reuse to the API and end that session; sticky routing per session avoids it), the portal's per-session export limit is counted softly in the shared cache (no atomic increment; the API's `Throttle:ExportsPerMinute` is the hard cap and the portal shows its 429), and permissions in a session stay a snapshot from sign-in.

### Operations tooling
* Load tests: `tests/Load/README.md` (k6 script and a .NET driver; budgets from docs/01 section 10).
* Backup, restore and the drill: `docs/runbooks/backup-restore.md`, `deploy/scripts/backup-restore-drill.sh`; `Migrator verify-ledger` (exit 3 when the ledger is broken; needs the production `Encryption__MasterKeyBase64`).

## Development-only switches (the API refuses to start in Production with any of these on)
`Jwt:AllowEphemeralKey`, `Encryption:AllowEphemeralKey`, `Email:LogBodies` (logs reset links!). In Development `Mfa:RequiredPlatformRoles` is `-` (see above).

## Migrator commands
`migrate` · `script` (idempotent SQL for DBAs) · `app-principal` (`Migrator:AppLogin`, `Migrator:AppPassword`) · `recover-superadmin` · `verify-ledger` (recompute every license ledger incl. signed checkpoints; exit 0 clean, 3 broken, 1 could not run; used by the restore drill).

## Tests only
`TEST_SQL_CONNECTION` — use an existing SQL Server instead of Testcontainers.

Later milestones append: face-provider settings, license rule defaults, retention windows, webhook limits.
