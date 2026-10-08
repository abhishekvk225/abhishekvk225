# 04 — Security Strategy

> Owners: Security Agent (review/sign-off) · Solution Architect Agent · Status: **Draft v1**
> Target: **OWASP ASVS Level 2**, OWASP Top 10 (2021) coverage, plus biometric-privacy controls.

## 1. Threat model summary (STRIDE, top risks)

| # | Threat | Asset | Primary controls |
|---|---|---|---|
| T1 | Cross-tenant data access (IDOR, missing filter, forged tenant id) | All tenant data, biometrics | Tenant from credential only; EF filters + write guard + SQL RLS; 404-not-403; isolation test-suite as a release gate |
| T2 | Credential theft / stuffing / brute force | Accounts | Identity hashing (PBKDF2-HMAC-SHA512 ≥ 210k, upgradeable), lockout, per-IP+account rate limits, generic errors, breached-password check (optional), MFA-ready |
| T3 | API key leakage or guessing | Client API access, license credits | 256-bit random keys, SHA-256 at rest, show-once, prefix lookup + constant-time compare, scopes, optional IP allow-list, expiry, instant revoke, `last used` visibility, secret-scanning guidance |
| T4 | License abuse (double spend, race, replay, negative balance) | Revenue | Conditional atomic UPDATE, DB CHECK constraints, idempotency keys, immutable hash-chained ledger, reconciliation job |
| T5 | Token theft (XSS → token) | Sessions | BFF: tokens never in browser; HttpOnly+Secure+SameSite cookie; strict CSP; 15-min access tokens; refresh rotation with reuse detection |
| T6 | Biometric data breach | Face templates/images | Envelope AES-256-GCM per client, templates never returned by APIs, images not retained by default, retention + erasure, crypto-shredding, encrypted backups |
| T7 | Malicious upload (polyglot, decompression bomb, EXIF leak, path traversal) | Server | Magic-byte sniffing, size/pixel caps, decode → re-encode, GUID blob keys outside web root, no user-controlled paths, AV scan hook |
| T8 | SSRF via webhook URL | Internal network | https-only, public-IP-only resolution at send time, no redirects, timeouts, allow/deny config |
| T9 | Tampering with audit/ledger | Accountability | DENY UPDATE/DELETE + triggers + hash chain + nightly verification + **HMAC-SHA256 checkpoints** (key from the master key provider, purpose `ledger-anchor`, outside the database) written only after a license verified clean and checked on every run (detects a recomputed unkeyed chain, tail truncation, deleted rows and whole-license deletion); persistent breaks audited once, logged Critical every run; checkpoints also logged (event 7002) for off-database retention |
| T10 | DoS / resource exhaustion | Availability | Shared (cross-node) rate limits and daily quota, per-principal throttles on dashboards/exports/webhook actions, per-endpoint webhook fairness, Rate limits, body-size limits, request timeouts, bounded paging, concurrency limit on face engine, health-based shedding |
| T11 | Privilege escalation via role/permission edit | Admin plane | System roles immutable, scope-checked grants (cannot grant ≥ own permissions), all RBAC changes audited; client user managers can only change users with strictly less access (owner excepted); **two-person approval** for credit adjustments above `Licensing:MaxAdjustPerAction`; MFA mandatory for Super Admins |
| T12 | Secrets in repo/logs | Keys, connection strings | Secret stores only, startup validation, log redaction, gitleaks in CI |

## 2. Authentication
- **Passwords**: ASP.NET Core Identity; min length 12, no composition rules theatre, deny list of common passwords; `PasswordHasher` configured to OWASP iteration counts; rehash-on-login when parameters increase.
- **Lockout**: 5 failures → 15-min lockout (configurable), plus IP-based throttle; every attempt written to `LoginHistory`.
- **Tokens**: access JWT 15 min (asymmetric, `kid`, key rotation, `iss/aud/exp/nbf` validated, clock skew ≤ 1 min, `alg` pinned); refresh tokens opaque 256-bit, stored hashed, 7-day sliding with absolute 30-day cap, rotation + family revocation on reuse.
- **Session invalidation**: password change/reset, role change, client suspension, user deactivation → bump `SecurityStamp` / revoke refresh tokens; JWT carries `sv` and the hot path checks a 30-second cache.
- **Password reset**: single-use, 30-min tokens (Identity data-protection tokens), identical response whether account exists, notification email to account owner.
- **MFA (M9a)**: TOTP RFC 6238 (HMAC-SHA1, 30 s, 6 digits, ±1 step) for every user (platform and client). The secret is generated on the server, shown once as `otpauth://` URI + base32 (the portal draws the QR code itself, nothing leaves the server) and stored **AES-256-GCM encrypted** with a key derived from the master key (purpose `mfa-secret`, authenticated with the user id). Enrolment is confirmed with a code (5 wrong codes discard the pending setup) and yields 10 single-use recovery codes (SHA-256 at rest, shown once, regenerable with a live code). Sign-in becomes two-step: password → short-lived (5 min), single-use challenge ticket (hash at rest, bound to the user, not to an IP) → `POST /auth/mfa/verify`. Brute force: every code attempt is counted **atomically before** it is checked (5 per challenge) and also reserves an attempt on the account throttle, which is **not** cleared by the password step, so "log in, guess, repeat" cannot out-run the lockout; an accepted TOTP step is recorded atomically and can never be used again (replay). `security.requireMfa`: platform default `Mfa:RequiredPlatformRoles=SuperAdmin` and each client's own setting; an account that must enrol receives a token with the `mer` claim that carries **no permissions** (same pattern as a forced password change). Disable/reset only by *another* Super Admin with a reason (audit `auth.mfa_reset`, sessions end). The portal keeps the challenge on the server (cookie `__Host-nv.mfa` is an opaque id) — the browser never holds a token or the challenge.
- **API key auth handler**: parses `X-Api-Key`, looks up by prefix (compiled query), constant-time hash compare, checks status/expiry/IP allow-list, loads client status via cached guard, builds principal with `cid`, `actor=apikey`, `scope` permissions. Failures are uniform (`API_KEY_INVALID`) and throttled per IP.

## 3. Authorization (RBAC, extensible)

**Model**: `User —< UserRoles >— Role —< RolePermissions >— Permission`. Code declares permission keys (`Contracts.Permissions`); a dynamic `IAuthorizationPolicyProvider` builds a policy per key, so endpoints use `[HasPermission(Permissions.Licenses.Manage)]`. A `PermissionAuthorizationHandler` evaluates the principal's roles against a cached `role → permissions` map (invalidated on RBAC change). **Adding a role = inserting data**; adding a permission = one constant + seed sync.

**Scope rule**: every permission has a scope (`Platform`, `Client`, `Both`). Platform permissions can never be granted to Client-scope roles (validated in service and by a DB check on seed). API-key scopes are a subset of the creator's permissions at creation time.

### Permission catalogue (initial)
`clients.read|create|update|manage-status|reset-password|settings` · `licenses.read|create|update|manage-status|renew|adjust|cost-rules|verify-ledger` · `plans.manage` · `dashboard.admin` · `dashboard.client` · `reports.read` · `audit.read` · `audit.read.client` · `system.configure` · `roles.manage` · `users.platform-manage` · `users.manage` · `client.profile.read|update` · `license.read` · `usage.read` · `apikeys.read|manage` · `apilogs.read` · `settings.recognition|notifications|security` · `webhooks.manage` · `notifications.read` · `faces.enroll|verify|identify|detect|read|manage|erase|history`

### Default role matrix
| Capability | Super Admin | Client Admin | Client User |
|---|:-:|:-:|:-:|
| Create/edit/suspend clients, reset client passwords | ✅ | – | – |
| Generate/renew/adjust/suspend licenses, cost rules, plans | ✅ | – | – |
| Admin dashboard, global reports, audit logs, system config, RBAC | ✅ | – | – |
| Client dashboard & usage | (via client drill-down) | ✅ | ✅ (read-only summary) |
| View own license & ledger | – | ✅ | ✅ (balance/expiry only) |
| Manage API keys, webhooks, API logs | – | ✅ | – |
| Client settings (profile, security, recognition, notifications) | – | ✅ | – |
| Manage client users | – | ✅ | – |
| Enroll/verify/identify faces | – | ✅ | ✅ (`faces.verify`, `faces.identify`, `faces.history`; `faces.enroll` configurable) |
| Erase face profiles | – | ✅ | – |
| Client audit & login history | ✅ (any client) | ✅ (own) | – |

Super Admin has **no** implicit access to biometric content of clients: face operations on behalf of a client are not granted to platform roles (support impersonation, if ever added, is a separate time-boxed, audited, client-approved permission).

## 4. Multi-tenant isolation (see `docs/01` §6)
Mandatory automated tests (release gate): for each tenant-owned entity and each endpoint, a user/API key of client A must receive 404/empty when addressing client B's ids; mass-assignment of `ClientId` in bodies is ignored; RLS blocks raw SQL cross-reads; background jobs cannot run without a tenant scope; query-filter bypass (`IgnoreQueryFilters`) only in whitelisted platform classes (architecture test).

## 5. Transport, headers, CORS, CSRF, XSS
- HTTPS only; HSTS (1 year) from the security-headers middleware; plain HTTP is redirected (health probes exempt) by an own middleware that honours **trusted** forwarded headers (`ForwardedHeaders:KnownProxies/KnownNetworks`; enabling it without a trust list aborts startup); TLS 1.2+; Kestrel server header removed; production refuses `AllowedHosts: *`.
- Headers (middleware, config-driven): `Content-Security-Policy` (default-src 'self'; no inline script except nonce'd Blazor bootstrap; `frame-ancestors 'none'`; `img-src 'self' data: blob:`; `connect-src 'self' wss:`), `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy: camera=(self), microphone=(), geolocation=()`, `Cross-Origin-Opener-Policy: same-origin`, `Cache-Control: no-store` on authenticated/API responses.
- **CORS**: the portal is BFF (same-origin to its own server) so the API's CORS policy is an **explicit allow-list per environment from configuration** — never `*` with credentials; integrators calling the API server-to-server don't need CORS. Browser-based integrators must be listed per client (`integration.allowedOrigins`).
- **CSRF**: portal uses antiforgery tokens on every state-changing form/endpoint; bearer-token and API-key API calls are not cookie-authenticated, hence CSRF-immune. Cookie: `HttpOnly; Secure; SameSite=Lax` (Strict for admin).
- **XSS**: Blazor encodes by default; `MarkupString`/`innerHTML` banned except via a single sanitising component (analyzer rule); user-supplied text (client name, metadata) is always rendered as text; CSV exports escape leading `= + - @`.
- **Clickjacking**: CSP `frame-ancestors 'none'` + `X-Frame-Options: DENY`.

## 6. Input validation & injection
- FluentValidation on every request DTO (lengths, formats, enums, ranges, collection sizes); unknown/extra JSON members rejected for security-sensitive DTOs; model-binding over-posting prevented by dedicated request records (never bind entities).
- **SQL injection**: EF Core parameterised queries only; raw SQL only via `FromSql`/`ExecuteSql` interpolation or explicit parameters; analyzer + review rule: no string-concatenated SQL.
- Path/URL handling: no file paths from users; blob keys are server-generated GUIDs.
- JSON depth/size limits, multipart limits, header size limits, request timeouts.

## 7. Secrets & cryptography
| Item | Mechanism |
|---|---|
| Config secrets (DB, JWT signing key, master key) | `dotnet user-secrets` (dev) → environment variables / Azure Key Vault / Docker secrets (prod). **Nothing sensitive in `appsettings*.json` or the repo.** Startup validation fails fast if missing. |
| Data keys | per-client DEK (AES-256), wrapped by master key (KEK) via `IKeyProvider`; KEK rotation re-wraps DEKs only |
| Face templates, webhook secrets | AES-256-GCM, random 96-bit nonce, `ClientId` + `KeyVersion` as AAD (prevents ciphertext transplant between tenants) |
| Platform-level secrets (staff/user MFA secrets, ledger anchor MAC) | HKDF-SHA256 sub-keys of the master key per purpose (`mfa-secret`, `ledger-anchor`); AES-256-GCM with the owning record id as AAD / HMAC-SHA256. Rotating the master key means re-enrolling MFA and re-anchoring the ledger (documented in `deploy/CONFIG.md`) |
| API keys / refresh tokens | random 256-bit (CSPRNG), stored as SHA-256; raw value shown once |
| Passwords | Identity hasher (PBKDF2-HMAC-SHA512, 210,000 iterations — the OWASP figure for SHA-512; configurable and auto-upgraded) |
| Randomness | `RandomNumberGenerator` only |
| Backups | encrypted at rest (TDE on SQL Server) + key separation from the DB |
- Secrets are excluded from logs/audit/ProblemDetails via a Serilog destructuring policy + `[Sensitive]`/`[AuditIgnore]` attributes + unit tests asserting redaction.

## 8. Rate limiting, abuse, availability
See `docs/03` §7. Implemented in M1/M2: global per-IP limiter, a much stricter per-IP policy on credential endpoints, request-body/header/time limits, JSON depth limit, request timeouts. Additional: concurrency limiter around the face engine (bounded queue → 503 with `Retry-After` rather than unbounded memory), request body limits (≤ 6 MB on face endpoints), slow-request timeouts.

## 9. Biometric privacy & compliance (design obligations)
- Consent reference **required** on enrollment; stored with timestamp. The client is the controller, the platform is the processor — Terms/DPA must say so (non-code task for the business).
- Data minimisation: raw images **not retained** by default; templates only; retention days configurable per client within platform max; `RetentionPurgeJob` hard-deletes expired profiles.
- Right to erasure: `DELETE /faces/profiles/{id}` + client-offboarding crypto-shredding; audit entry keeps only ids.
- Embeddings never leave the server (no endpoint returns them); no biometrics in logs, URLs, error messages or analytics.
- Region/residency: single region v1; `Clients.DataRegion` reserved in design for later.
- Legal review (GDPR Art. 9, BIPA, etc.) and a DPIA template are on the pre-launch checklist.

## 10. Logging, audit & monitoring
- Serilog structured JSON; enrichers: `CorrelationId`, `ClientId`, `ActorType/Id`, `RouteTemplate`; levels configurable; request logging excludes bodies/headers/query strings; PII minimised (emails hashed or masked in info-level logs).
- **Audit events** (minimum): login success/failure/lockout, password change/reset, client create/edit/status changes, user create/deactivate/role change, license create/status/renew/adjust, every ledger deduction (via ledger), API key create/rotate/revoke, settings changes (old/new), webhook changes, face profile erasure, RBAC changes, exports, system-settings changes.
- Alerting (Notifications + external via OpenTelemetry): login-failure spike, 401/403 spike per key, 5xx rate, provider failure rate, license-ledger chain mismatch, unusual consumption (> N× 7-day average), job failures.

## 11. Secure SDLC & supply chain
- CI gates: build with warnings-as-errors + analyzers (Roslyn security analyzers, `Microsoft.CodeAnalysis.NetAnalyzers`), unit/integration/isolation tests, **CodeQL**, `dotnet list package --vulnerable`, dependency-review, container image scan, **gitleaks**, SBOM (CycloneDX).
- Central package management with pinned versions; Dependabot/Renovate.
- Pre-release: independent penetration test, ASVS-L2 checklist sign-off, restore-from-backup drill (`docs/runbooks/backup-restore.md`, `deploy/scripts/backup-restore-drill.sh`; automated in `BackupRestoreDrillTests`; `Migrator verify-ledger` proves the restored ledger and needs the production master key).
- **Security Agent review gate** per module (see `docs/05`): authN/Z paths, tenant isolation, secrets, validation, error leakage, logging, crypto usage — findings tracked; Critical/High block completion.

## 12. OWASP Top 10 (2021) mapping
| Risk | Where addressed |
|---|---|
| A01 Broken Access Control | §3, §4, 404-not-403, permission policies on every endpoint (architecture test: no endpoint without `[Authorize]`/policy unless in an allow-list) |
| A02 Cryptographic Failures | §7, TLS, TDE, no custom crypto primitives |
| A03 Injection | §6 |
| A04 Insecure Design | threat model §1, ADRs, abuse-case tests (license races, replay) |
| A05 Security Misconfiguration | §5 headers, config validation, no dev features in prod, Swagger/OpenAPI served only in Development, hardened containers (non-root, read-only FS) |
| A06 Vulnerable Components | §11 |
| A07 Identification & Auth Failures | §2 |
| A08 Software & Data Integrity | CI signing/SBOM, immutable ledger/audit, signed webhooks, no deserialization of untrusted types |
| A09 Logging & Monitoring Failures | §10 |
| A10 SSRF | webhook guard (`docs/03` §8), no user-controlled outbound URLs elsewhere |

## 13. Review log
| Date | Scope | Verdict | Notes |
|---|---|---|---|
| M1 | Foundation security review | PASS-WITH-CONDITIONS | 8 conditions (strict principal parsing, scope restriction, session-context refresh, forwarded headers, nullable tenant decision, model coverage rules, least-privilege DB login, atomic RLS install) — all addressed in the M1 hardening pass; re-verification pending in the M2 security gate |
| M2 | Identity & Access security review | FAIL → fixed | H-1 concurrent lockout bypass (atomic reserve-before-verify counter), H-2 delegated admin could demote Super Admin (target-privilege + last-Super-Admin guard + break-glass), plus refresh-rotation race (atomic claim + grace window), lockout-as-DoS (lockout blocks password sign-in only), change-password lockout, forgot-password timing/bombing (constant-time padding + cooldown + background email), role-edit and RBAC propagation, RLS deploy window (`SCHEMABINDING OFF`), least-privilege DB principal enforced by readiness. Re-verification pending. |

| M9a | Hardening (security features) | implemented, pending Security review | MFA (TOTP, recovery codes, throttling, replay protection, enforcement), two-person approval for large credit adjustments, keyed ledger anchors + async verification + alert de-duplication, emergency API controls (revoke all/one, kill switch), client-view redaction of platform staff, target-privilege rule in client user management, security-m6 L-1/L-3/L-4/L-6/L-7/L-9, security-m5 open items (atomic profile cap, history personal-data retention, drained sweeper) — see STATUS.md |
| M9b | Hardening (scalability) | implemented, pending Security review | Shared SQL rate-limit/quota counters, per-principal throttles, webhook fairness + failed-event counting, ledger alert-once with operator list, CSV failure handling + BOM, shared portal cache option (SQL Server / Redis), load tooling, restore drill + runbook, portal header allow-list — see STATUS.md |

### Known limits (documented, accepted)
- Session/permission/client-status caches are per instance: revocation reaches other instances within ≤ 30 s (sessions) / 60 s (role→permission map).
- Access tokens (15 min) cannot be revoked individually; password change, deactivation, suspension and role/permission removal invalidate them through the security version.
- The in-process email queue is lost on a crash; the notifications module replaces it with a durable outbox.
- API-key revocation, edits and the client kill switch reach **other nodes within `ApiAuth:CacheSeconds` (default 5 s)**; the node that handled the change applies it immediately.
- Ledger anchors defeat an attacker who can rewrite rows and recompute the unkeyed chain, truncate the tail or delete a license — but not one who can also delete the newest checkpoint rows (append-only trigger/DENY must be bypassed first) *and* the matching newest ledger rows. Mitigation: the checkpoint log line (event 7002) should be shipped to storage the database operator cannot alter (WORM/log pipeline); `Metering:LedgerVerification` alerts on any break.
- The portal's MFA challenge, session store and export throttle are per node unless `PortalCache:Provider` is `SqlServer` or `Redis` (M9b; payloads are Data-Protection encrypted before they reach the cache, the key ring must be shared too). The single-flight token refresh is still per process: keep a session on one node (sticky) or accept that two nodes refreshing at the same instant look like token reuse and end that session.
- API rate-limit/quota counters (M9b) are shared through SQL Server; with `Counters:Shared=false`, or while the database is unreachable, each node counts on its own (limits multiply by the number of nodes). Permits a node reserved and did not use are lost for that window.
- A persistent ledger break raises the operator alert once (Critical log 7001, audit entry, open record in `GET /admin/licensing/ledger-breaks`) and then only as a reminder every `ReminderDays` (default 7; 0 = never). Alerting must key on event 7001 **and** on the open-break list; event 7003 (Warning) is the nightly "still unresolved" note.
- Recovery of the last Super Admin who lost their authenticator and has no recovery codes needs a database-side reset (`iam.UserMfa`, `iam.MfaRecoveryCodes`, `Users.TwoFactorEnabled`) followed by `Migrator recover-superadmin`; there is deliberately no self-service bypass.
