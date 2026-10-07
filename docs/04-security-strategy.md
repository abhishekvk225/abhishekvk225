# 04 — Security Strategy

> Owners: Security Agent (review/sign-off) · Solution Architect Agent · Status: **Draft v1**
> Target: **OWASP ASVS Level 2**, OWASP Top 10 (2021) coverage, plus biometric-privacy controls.

## 1. Threat model summary (STRIDE, top risks)

| # | Threat | Asset | Primary controls |
|---|---|---|---|
| T1 | Cross-tenant data access (IDOR, missing filter, forged tenant id) | All tenant data, biometrics | Tenant from credential only; EF filters + write guard + SQL RLS; 404-not-403; isolation test-suite as a release gate |
| T2 | Credential theft / stuffing / brute force | Accounts | Identity hashing (PBKDF2-SHA512 ≥ 600k, upgradeable), lockout, per-IP+account rate limits, generic errors, breached-password check (optional), MFA-ready |
| T3 | API key leakage or guessing | Client API access, license credits | 256-bit random keys, SHA-256 at rest, show-once, prefix lookup + constant-time compare, scopes, optional IP allow-list, expiry, instant revoke, `last used` visibility, secret-scanning guidance |
| T4 | License abuse (double spend, race, replay, negative balance) | Revenue | Conditional atomic UPDATE, DB CHECK constraints, idempotency keys, immutable hash-chained ledger, reconciliation job |
| T5 | Token theft (XSS → token) | Sessions | BFF: tokens never in browser; HttpOnly+Secure+SameSite cookie; strict CSP; 15-min access tokens; refresh rotation with reuse detection |
| T6 | Biometric data breach | Face templates/images | Envelope AES-256-GCM per client, templates never returned by APIs, images not retained by default, retention + erasure, crypto-shredding, encrypted backups |
| T7 | Malicious upload (polyglot, decompression bomb, EXIF leak, path traversal) | Server | Magic-byte sniffing, size/pixel caps, decode → re-encode, GUID blob keys outside web root, no user-controlled paths, AV scan hook |
| T8 | SSRF via webhook URL | Internal network | https-only, public-IP-only resolution at send time, no redirects, timeouts, allow/deny config |
| T9 | Tampering with audit/ledger | Accountability | DENY UPDATE/DELETE + triggers + hash chain + nightly verification |
| T10 | DoS / resource exhaustion | Availability | Rate limits, body-size limits, request timeouts, bounded paging, concurrency limit on face engine, health-based shedding |
| T11 | Privilege escalation via role/permission edit | Admin plane | System roles immutable, scope-checked grants (cannot grant ≥ own permissions), all RBAC changes audited |
| T12 | Secrets in repo/logs | Keys, connection strings | Secret stores only, startup validation, log redaction, gitleaks in CI |

## 2. Authentication
- **Passwords**: ASP.NET Core Identity; min length 12, no composition rules theatre, deny list of common passwords; `PasswordHasher` configured to OWASP iteration counts; rehash-on-login when parameters increase.
- **Lockout**: 5 failures → 15-min lockout (configurable), plus IP-based throttle; every attempt written to `LoginHistory`.
- **Tokens**: access JWT 15 min (asymmetric, `kid`, key rotation, `iss/aud/exp/nbf` validated, clock skew ≤ 1 min, `alg` pinned); refresh tokens opaque 256-bit, stored hashed, 7-day sliding with absolute 30-day cap, rotation + family revocation on reuse.
- **Session invalidation**: password change/reset, role change, client suspension, user deactivation → bump `SecurityStamp` / revoke refresh tokens; JWT carries `sv` and the hot path checks a 30-second cache.
- **Password reset**: single-use, 30-min tokens (Identity data-protection tokens), identical response whether account exists, notification email to account owner.
- **MFA**: TOTP scaffolding in schema/flows (`TwoFactorEnabled`, `security.requireMfa` setting); enforced for Super Admin before go-live (roadmap M8).
- **API key auth handler**: parses `X-Api-Key`, looks up by prefix (compiled query), constant-time hash compare, checks status/expiry/IP allow-list, loads client status via cached guard, builds principal with `cid`, `actor=apikey`, `scope` permissions. Failures are uniform (`API_KEY_INVALID`) and throttled per IP.

## 3. Authorization (RBAC, extensible)

**Model**: `User —< UserRoles >— Role —< RolePermissions >— Permission`. Code declares permission keys (`Contracts.Permissions`); a dynamic `IAuthorizationPolicyProvider` builds a policy per key, so endpoints use `[HasPermission(Permissions.Licenses.Manage)]`. A `PermissionAuthorizationHandler` evaluates the principal's roles against a cached `role → permissions` map (invalidated on RBAC change). **Adding a role = inserting data**; adding a permission = one constant + seed sync.

**Scope rule**: every permission has a scope (`Platform`, `Client`, `Both`). Platform permissions can never be granted to Client-scope roles (validated in service and by a DB check on seed). API-key scopes are a subset of the creator's permissions at creation time.

### Permission catalogue (initial)
`clients.read|create|update|manage-status|reset-password|settings` · `licenses.read|create|update|manage-status|renew|adjust|cost-rules` · `plans.manage` · `dashboard.admin` · `dashboard.client` · `reports.read` · `audit.read` · `audit.read.client` · `system.configure` · `roles.manage` · `users.platform-manage` · `users.manage` · `client.profile.read|update` · `license.read` · `usage.read` · `apikeys.read|manage` · `apilogs.read` · `settings.recognition|notifications|security` · `webhooks.manage` · `notifications.read` · `faces.enroll|verify|identify|detect|read|manage|erase|history`

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
| API keys / refresh tokens | random 256-bit (CSPRNG), stored as SHA-256; raw value shown once |
| Passwords | Identity hasher (PBKDF2-SHA512) |
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
- Pre-release: independent penetration test, ASVS-L2 checklist sign-off, restore-from-backup drill.
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
