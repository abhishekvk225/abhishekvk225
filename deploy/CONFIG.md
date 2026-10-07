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

## Development-only switches (the API refuses to start in Production with any of these on)
`Jwt:AllowEphemeralKey`, `Encryption:AllowEphemeralKey`, `Email:LogBodies` (logs reset links!).

## Migrator commands
`migrate` · `script` (idempotent SQL for DBAs) · `app-principal` (`Migrator:AppLogin`, `Migrator:AppPassword`) · `recover-superadmin`.

## Tests only
`TEST_SQL_CONNECTION` — use an existing SQL Server instead of Testcontainers.

Later milestones append: face-provider settings, license rule defaults, retention windows, webhook limits.
