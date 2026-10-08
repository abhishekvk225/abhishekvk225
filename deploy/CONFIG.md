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

## Usage, dashboards and alerts (M7)
| Key | Default | Notes |
|---|---|---|
| `Dashboards:DefaultDays` / `MaxDays` | 30 / 90 | Window of the client and admin dashboards; `days` outside 1..`MaxDays` is rejected with 400. |
| `Dashboards:TopApiKeys` / `TopClients` / `AttentionListSize` | 5 / 10 / 10 | Rows in the "top" lists and the expiring / low-balance lists (their counts are always complete). |
| `Dashboards:ExpiringWithinDays` / `LowBalancePercent` | 30 / 10 | Admin "expiring" horizon and the "low balance" threshold (share of credits left). |
| `Dashboards:LatencyBucketMilliseconds` / `LatencyCapMilliseconds` | 25 / 10000 | The p95 latency is read off a histogram of this resolution (exact to one bucket); slower requests count in the last bucket. |
| `Dashboards:DefaultReportDays` / `MaxReportDays` | 30 / 92 | Default and maximum range of one usage CSV export. |
| `Metering:LedgerVerification:Enabled` | `true` | Nightly tamper check of every license ledger (hash chain + balance). Findings are logged at **Critical** and audited as `ledger.verification_failed`: alert on that log level. The on-demand `POST /api/v1/admin/licensing/verify-ledger` works even when this is off. |
| `Metering:LedgerVerification:IntervalHours` / `InitialDelayMinutes` | 24 / 10 | Run cadence and the wait after start-up (so a restart does not skip the check). |
| `Metering:LedgerVerification:LicenseBatchSize` / `EntryBatchSize` | 200 / 1000 | Paging of the read-only scan. |
| `Metering:LedgerVerification:BalanceRecheckAttempts` / `RecheckDelayMilliseconds` | 2 / 250 | A balance-only mismatch is re-checked (a charge may have landed mid-scan) before being reported; a broken row is reported at once. |
| `Metering:Alerts:Enabled` | `true` | Hourly job raising `license.low_balance`, `license.exhausted`, `license.expiring`, `license.expired` and `apikey.expiring` (webhook + in-app notification). Set `false` in tests. |
| `Metering:Alerts:IntervalMinutes` / `InitialDelaySeconds` | 60 / 60 | |
| `Metering:Alerts:LowBalancePercent` | 10 | Credits left (or less) at which the low-balance alert fires. |
| `Metering:Alerts:ExpiringNoticeDays` / `ExpiringFinalNoticeDays` | 7 / 1 | Days before the end date of a license of the two expiry notices (final must be shorter). |
| `Metering:Alerts:ApiKeyExpiringDays` | 7 | |
| `Metering:Alerts:ExpiredLookbackDays` | 3 | A license that ended longer ago is not announced (no history dump on first run). |
| `Metering:Alerts:BatchSize` | 500 | Page size of the candidate scan. |

Alerts are de-duplicated by a unique `(subject, type, bucket)` row (`licensing.LicenseAlerts`), so restarts and several API nodes cannot send one twice; a renewal or top-up changes the bucket so the next crossing alerts again. The job runs on every node that hosts the API; split it onto a worker later without code change.

## Development-only switches (the API refuses to start in Production with any of these on)
`Jwt:AllowEphemeralKey`, `Encryption:AllowEphemeralKey`, `Email:LogBodies` (logs reset links!).

## Migrator commands
`migrate` · `script` (idempotent SQL for DBAs) · `app-principal` (`Migrator:AppLogin`, `Migrator:AppPassword`) · `recover-superadmin`.

## Tests only
`TEST_SQL_CONNECTION` — use an existing SQL Server instead of Testcontainers.

Later milestones append: face-provider settings, license rule defaults, retention windows, webhook limits.
