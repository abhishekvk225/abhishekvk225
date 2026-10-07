# Configuration catalogue

Layering: `appsettings.json` (safe defaults, **no secrets**) → `appsettings.{Environment}.json` → environment variables
(`Section__Key`) / secret store → command line. Required values fail fast at startup.

| Key | Purpose | Default | Secret | Notes |
|---|---|---|---|---|
| `ConnectionStrings:Default` | SQL Server connection | — (required) | **yes** | user-secrets in dev; env var / Key Vault / Docker secret in prod |
| `Database:CommandTimeoutSeconds` | EF command timeout | 30 | no | 1–600 |
| `Hosting:RedirectToHttps` | HTTP→HTTPS redirect | `true` (`false` in Development) | no | disable only when TLS terminates at a trusted proxy that redirects |
| `SecurityHeaders:*` | CSP, Referrer-Policy, Permissions-Policy, HSTS max-age | strict API defaults | no | `HstsMaxAgeSeconds=0` disables HSTS |
| `Cors:AllowedOrigins` | Browser origins allowed to call the API | `[]` (none) | no | explicit origins only; `*` aborts startup |
| `RequestLimits:MaxRequestBodyBytes` | Max request body | 6 MiB | no | align with IIS `maxAllowedContentLength` |
| `Serilog:*` | Log levels/sinks | Information, compact JSON to console | no | add sinks via config |
| `TEST_SQL_CONNECTION` (tests only) | Use an existing SQL Server instead of Testcontainers | unset | yes | CI/dev boxes without Docker |

Later milestones append: JWT signing keys, key-encryption master key, face-provider settings, rate-limit defaults, retention windows.
