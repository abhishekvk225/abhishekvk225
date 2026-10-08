# Runbook: IIS deployment (Windows Server)

Package: `scripts/publish-iis.ps1` (CI job `iis-package` and the release produce `nexaverify-iis-<version>.zip`).

```powershell
pwsh scripts/publish-iis.ps1 -Version 1.0.0          # add -SkipTests for a fast local package
```

Package layout: `api/`, `portal/`, `migrator/`, `sql/migrate.sql`, `VERSION.txt`, `build-info.json`, `SHA256SUMS`. The hardened `web.config` files come from `deploy/iis/web.config.api` and `web.config.portal`. The package holds no secrets or environment configuration.

## Prerequisites on the server

- Windows Server 2022+, IIS with **WebSocket Protocol** (needed by the Blazor portal) and **URL Rewrite not required**.
- **.NET 10 ASP.NET Core Hosting Bundle** (runtime + ASP.NET Core Module V2). Reboot or `iisreset` after installing.
- TLS certificates bound to the two sites (API host, portal host). Use TLS 1.2+ only; disable legacy protocols (IIS Crypto or policy).
- SQL Server (managed or separate) reachable over TLS with a trusted certificate. Two logins: an **administrative** one used only by the migrator, and the **least-privilege app login** created by `Migrator app-principal`.

## Sites and application pools

| | API | Portal |
|---|---|---|
| Site / host | `api.example.com` | `portal.example.com` |
| Physical path | `D:\sites\nexaverify\api` | `D:\sites\nexaverify\portal` |
| App pool | `NexaVerify-Api`, **No Managed Code**, identity `ApplicationPoolIdentity` | `NexaVerify-Portal`, same |
| Start mode | `AlwaysRunning`, site **Preload Enabled** | same |
| Idle time-out | **0** (background jobs: ledger verification, webhooks, alerts, retention must keep running) | 0 |
| Recycle | fixed time off-peak only; overlapped recycling disabled for the portal (in-memory sessions) | same |
| Bindings | https 443 with certificate; **no http binding** (or an http->https redirect site) | same |

Single worker process per pool (`Maximum Worker Processes = 1`).

## Configuration and secrets

Secrets are never put in `web.config`. Two options (both read through standard .NET configuration):

1. **Secret files (recommended):** create `C:\ProgramData\NexaVerify\secrets\api` and `...\portal`, grant *read* only to the matching app-pool identity (`IIS AppPool\NexaVerify-Api`) and Administrators. One file per setting, file name = setting name with `__` for `:`, content = value:
   `ConnectionStrings__Default`, `Jwt__SigningKeyPem`, `Encryption__MasterKeyBase64` (API); `DataProtection__KeyPath` can be a plain env var (portal). The path is `NEXAVERIFY_SECRETS_DIR` in `web.config` (default shown there).
2. **Machine environment variables** or Azure Key Vault configuration (add the provider in a later release). Environment variables are visible to administrators and process dumps; prefer files with tight ACLs.

Non-secret settings: the same files work for non-secret values, and keeping every environment-specific value in the config directory means a package update never overwrites configuration (`web.config` carries only `ASPNETCORE_ENVIRONMENT` and the directory path). Required in Production: `AllowedHosts` (real host name, never `*`), `Auth__PasswordResetUrlTemplate`, `Cors__AllowedOrigins__0` (portal origin), portal `Api__BaseUrl` (https), `DataProtection__KeyPath` (persistent, ACL-restricted folder), `Session__AllowInMemoryStore=true`. Full list: `deploy/CONFIG.md`.

TLS behind IIS: when IIS terminates TLS directly, leave `ForwardedHeaders:Enabled=false`. Behind a load balancer/ARR set `ForwardedHeaders__Enabled=true` and list the proxy address(es) in `ForwardedHeaders__KnownProxies__0` (never a `/0` network), otherwise per-IP rate limits and lockout history see only the proxy.

### Request limits

In-process hosting means **IIS enforces the request size**, not Kestrel. `maxAllowedContentLength` in the shipped `web.config` is 6 291 456 bytes for the API (equal to `RequestLimits:MaxRequestBodyBytes`; a 5 MB image plus multipart overhead fits) and 8 388 608 for the portal. Change both together. IIS "Request Filtering" also caps URL (2048) and query string (2048).

### Sticky sessions / ARR (Blazor Server)

The portal keeps session and circuit state in memory. One server: nothing to do. Several servers behind ARR or a load balancer: enable **client affinity (cookie)** and WebSocket pass-through, and share the Data Protection key ring (`DataProtection__KeyPath` on a share the pool identity can write). A node failure ends the sessions on that node; users sign in again.

## First deployment

1. Verify the zip: `Get-FileHash` against the SHA-256 in the release notes; unzip to a staging folder; check `SHA256SUMS`.
2. **Database:** either run the migrator (`dotnet migrator\NexaVerify.Migrator.dll migrate` then `... app-principal` with an admin `ConnectionStrings__Default`, `Seed__SuperAdminEmail/Password`, `Migrator__AppLogin/AppPassword` set for that process only), or hand `sql\migrate.sql` to the DBA (idempotent; run in a maintenance window after a backup). See [db-migration](db-migration.md). The site never migrates the database.
3. Stop the site (or place `app_offline.htm` in the folder), copy `api\*` and `portal\*` over the site folders (keep `logs\`), start the pools.
4. Smoke test from any host: `scripts/smoke-test.sh https://api.example.com https://portal.example.com` (needs bash+curl; or call `/health/live`, `/health/ready` and open `/login`).
5. Sign in as the seeded Super Admin; change the password; enrol MFA (required for Super Admin).

## Updates and rollback

- Update: same as steps 1-4; migrations first (additive), then files. Keep the previous package folder as `...\releases\<version>` and the last known-good site folder copy.
- Rollback: put `app_offline.htm`, restore the previous site folder (not the database), remove `app_offline.htm`, run the smoke test. Schema changes are expand/contract so the previous version runs on the newer schema; otherwise restore the database backup ([rollback](rollback.md)).

## Logging

The ASP.NET Core Module can capture stdout to `.\logs\stdout*` (disabled in the shipped `web.config`; enable `stdoutLogEnabled` only to debug a failed startup, then disable again). For production add a file or event-log sink and ship it per [monitoring](monitoring.md), including event 7002.

## Not verified

The script and `web.config` templates were exercised on Linux only (publish for `win-x64` succeeded; the package script itself was not run end to end and no IIS host was available). Treat the first Windows run as part of the staging rehearsal.
