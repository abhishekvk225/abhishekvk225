# 03 — API Specification (v1)

> Owners: Solution Architect Agent (contract) · Backend Agent (implementation) · Status: **Draft v1**
> The OpenAPI document is generated from the code (Swashbuckle/`Microsoft.AspNetCore.OpenApi`) and served at `/swagger` (non-prod) and `/openapi/v1.json`; this file is the contract the implementation is built and tested against.

## 1. Conventions

| Topic | Rule |
|---|---|
| Base path | `/api/v1` (URL-versioned; additive changes are non-breaking, breaking changes → `/v2`) |
| Formats | JSON (`camelCase`, UTC ISO-8601 timestamps, enums as strings). Image upload: `multipart/form-data`. |
| Auth | `Authorization: Bearer <jwt>` (portal) **or** `X-Api-Key: <key>` (integrations). Each endpoint lists accepted schemes + required permission. |
| Tenant | Never in the request. Derived from the credential. Admin endpoints address a client explicitly in the route. |
| Correlation | Request header `X-Correlation-Id` (optional) is echoed/generated on every response and in every log line and ProblemDetails. |
| Idempotency | `Idempotency-Key: <≤100 chars>` accepted on billable face endpoints and on `POST` license operations. Replay within 24 h returns the original response with `Idempotent-Replayed: true`. |
| Errors | RFC 7807 `application/problem+json` with extensions `code` (stable machine code), `correlationId`, `errors` (field → messages) on validation. Never stack traces, SQL or internal ids of other tenants. |
| Paging | `?page=1&pageSize=25&sort=createdAt:desc&search=…` → `{ items, page, pageSize, totalCount, totalPages }`. `pageSize` capped at 100. |
| Rate limit headers | `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`; `429` + `Retry-After`. |
| Not found vs forbidden | A resource belonging to another tenant returns **404** (never 403) so existence is not disclosed. |

### Error code catalogue (`Contracts.ErrorCodes`)

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `VALIDATION_FAILED` | Field errors in `errors` |
| 400 | `IMAGE_INVALID` / `IMAGE_TOO_LARGE` / `IMAGE_UNSUPPORTED_TYPE` | Bad upload (checked by magic bytes, not extension) |
| 401 | `UNAUTHENTICATED` / `TOKEN_EXPIRED` / `API_KEY_INVALID` / `API_KEY_EXPIRED` / `API_KEY_REVOKED` | |
| 402 | `LICENSE_NOT_FOUND` · `LICENSE_EXPIRED` · `LICENSE_INSUFFICIENT_BALANCE` | No usable license / expired / not enough credits |
| 403 | `FORBIDDEN` · `LICENSE_SUSPENDED` · `CLIENT_SUSPENDED` · `CLIENT_INACTIVE` · `IP_NOT_ALLOWED` | |
| 404 | `NOT_FOUND` | |
| 409 | `CONFLICT` · `DUPLICATE_EXTERNAL_REF` · `CONCURRENCY_CONFLICT` · `LICENSE_INVALID_TRANSITION` | |
| 413 | `PAYLOAD_TOO_LARGE` | |
| 422 | `NO_FACE_DETECTED` · `MULTIPLE_FACES` · `LOW_QUALITY_IMAGE` | Image processed, request not billable |
| 429 | `RATE_LIMITED` · `DAILY_QUOTA_EXCEEDED` | |
| 500 | `INTERNAL_ERROR` | Generic message + `correlationId` only |
| 502/503 | `FACE_PROVIDER_UNAVAILABLE` | Provider failure — **not billed**, `Retry-After` set |

## 2. Authentication & session — `/api/v1/auth` (anonymous unless noted; strictly rate-limited per IP + account)

| Method | Path | Purpose |
|---|---|---|
| POST | `/auth/login` | email + password → `{accessToken, expiresIn, refreshToken, user, mustChangePassword}`; records LoginHistory; generic error on any failure |
| POST | `/auth/refresh` | rotate refresh token (reuse detection revokes the family) |
| POST | `/auth/logout` 🔒 | revoke current refresh-token family |
| POST | `/auth/forgot-password` | always `202` (no account enumeration); emails a time-limited token |
| POST | `/auth/reset-password` | token + new password |
| POST | `/auth/change-password` 🔒 | current + new password; revokes other sessions |
| GET | `/auth/me` 🔒 | profile, role, permissions, client summary (name, status, timezone) |

JWT claims: `sub`, `jti`, `email`, `role`, `cid` (client id; absent for platform users), `sv` (security-stamp version), `iat/exp` (15 min). Signed with an asymmetric key (`kid` header, key rotation supported). Permissions are **not** in the token — resolved from a cached role→permission map so permission changes take effect without re-login.

## 3. Platform Admin API — `/api/v1/admin` (JWT; platform roles only)

### 3.1 Clients — permission `clients.*`
| Method | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/admin/clients` | `clients.read` | list/search/filter by status |
| POST | `/admin/clients` | `clients.create` | create client + first Client Admin (invite email or temporary password flagged `mustChangePassword`) + default settings + data key + optional initial license |
| GET | `/admin/clients/{id}` | `clients.read` | detail incl. license summary |
| PUT | `/admin/clients/{id}` | `clients.update` | edit details (concurrency via `If-Match`/rowVersion) |
| POST | `/admin/clients/{id}/activate` · `/deactivate` · `/suspend` | `clients.manage-status` | `suspend` requires `reason`; revokes refresh tokens; effect ≤ 30 s |
| POST | `/admin/clients/{id}/users/{userId}/reset-password` | `clients.reset-password` | returns nothing sensitive; sends reset link (or one-time temp password shown once) |
| GET | `/admin/clients/{id}/users` | `clients.read` | users of the client |
| GET | `/admin/clients/{id}/usage?from&to&granularity` | `usage.read` | usage series |
| GET | `/admin/clients/{id}/activity` | `audit.read` | audit entries for this client |
| GET | `/admin/clients/{id}/logins` | `audit.read` | login history for this client |
| GET/PUT | `/admin/clients/{id}/settings` | `clients.settings` | client-specific settings (platform-managed keys: limits, rate limits, provider pinning) |

### 3.2 Licenses — permission `licenses.*`
| Method | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/admin/licenses` | `licenses.read` | list/filter (client, status, expiring-in-N-days) |
| POST | `/admin/clients/{id}/licenses` | `licenses.create` | generate + assign (`planId?`, `totalCredits`, `startsAt`, `expiresAt`, `name`) → creates `Grant` ledger row |
| GET | `/admin/licenses/{id}` | `licenses.read` | detail + utilisation |
| PUT | `/admin/licenses/{id}` | `licenses.update` | name/notes/limits (credits can only change via adjustment) |
| POST | `/admin/licenses/{id}/activate` · `/deactivate` · `/suspend` · `/revoke` | `licenses.manage-status` | state transitions (`reason` required for suspend/revoke) |
| POST | `/admin/licenses/{id}/renew` | `licenses.renew` | new term and/or extra credits → `Renewal` ledger row |
| POST | `/admin/licenses/{id}/adjust` | `licenses.adjust` | ± credits with mandatory reason (`Adjustment` row); refund of a charge references the original transaction |
| GET | `/admin/licenses/{id}/transactions` | `licenses.read` | the ledger (read-only, paged) |
| GET | `/admin/licenses/{id}/usage` | `licenses.read` | consumption over time |
| GET/POST/PUT | `/admin/plans` … | `plans.manage` | plans CRUD |
| GET/PUT | `/admin/cost-rules` (+ `/clients/{id}/cost-rules`) | `licenses.cost-rules` | configure credits-per-operation & charge policy; new rules are versioned by `effectiveFrom` |

There is **no** endpoint to edit or delete a ledger entry, by design.

### 3.3 Dashboard, reports, system
| Method | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/admin/dashboard?days=30` (1..90) | `dashboard.admin` | one aggregate document (M7): clients/licenses by status, expiring ≤30 d and low-balance (≤10 %) licenses, credits per day (ledger), top 10 clients, API traffic, webhook health. Aggregates only — no face data |
| GET | `/admin/reports/usage.csv?from&to` (≤ 92 days) | `reports.read` | streamed CSV of billed usage per day/client/operation (CSV-injection-safe, audit-logged) |
| POST | `/admin/licensing/verify-ledger` | `licenses.verify-ledger` | recomputes every ledger hash chain; first broken row per license; 409 if one is already running (also runs nightly) |
| GET | `/admin/audit-logs` | `audit.read` | global audit search (actor, action, client, entity, date) |
| GET/PUT | `/admin/system-settings` | `system.configure` | platform defaults, retention, maintenance mode |
| GET | `/admin/system/health` | `system.configure` | provider status, job last-run, ledger-verification result |
| GET/POST/PUT | `/admin/roles` · `/admin/permissions` | `roles.manage` | list roles/permissions, create/edit non-system roles (extensibility hook) |
| GET/POST/PUT | `/admin/users` | `users.platform-manage` | platform staff accounts |

## 4. Client Portal API — `/api/v1/client` (JWT; tenant = `cid` claim, **no client id in path**)

| Method | Path | Permission (role) | Purpose |
|---|---|---|---|
| GET | `/client/dashboard?days=30` (1..90) | `dashboard.client` | one document (M7): license summary, recognitions per day/operation/outcome with success/no-match/error rates, credits per day (ledger), API requests/error rate/p95 per day, top API keys |
| GET | `/client/reports/usage.csv?from&to` (≤ 92 days) | `usage.read` | streamed CSV of the client's own usage per day/operation/outcome (CSV-injection-safe, audit-logged) |
| GET | `/client/profile` · PUT | `client.profile.read` / `client.profile.update` (ClientAdmin) | company info |
| GET | `/client/license` · `/client/licenses` · `/client/licenses/{id}/transactions` | `license.read` | balance, expiry, history (read-only) |
| GET | `/client/usage?from&to` | `usage.read` | detailed usage |
| GET/POST | `/client/users` · PUT `/{id}` · POST `/{id}/deactivate` · `/{id}/reset-password` | `users.manage` (ClientAdmin) | manage own users; cannot assign platform roles; cannot remove last admin |
| GET/POST | `/client/api-keys` | `apikeys.read` / `apikeys.manage` | POST returns the raw key **once** (`key`, `prefix`) |
| POST | `/client/api-keys/{id}/regenerate` · `/revoke` | `apikeys.manage` | rotation / revocation |
| GET | `/client/api-logs` | `apilogs.read` | request log viewer (filter by key, status, date) |
| GET/PUT | `/client/settings/recognition` | `settings.recognition` | threshold, retention, quality, max faces (bounded by platform limits) |
| GET/PUT | `/client/settings/notifications` | `settings.notifications` | low-balance %, expiry reminder days, email on/off |
| GET/PUT | `/client/settings/security` | `settings.security` | password policy extras, require MFA, IP allow-list |
| GET/POST/PUT/DELETE | `/client/webhooks` · POST `/{id}/test` · `/{id}/rotate-secret` | `webhooks.manage` | endpoints (secret shown once) |
| GET | `/client/webhooks/{id}/deliveries` | `webhooks.manage` | delivery attempts |
| GET | `/client/audit-logs` | `audit.read.client` | own activity history |
| GET | `/client/logins` | `audit.read.client` | own login history |
| GET | `/client/notifications?unreadOnly&page&pageSize` · POST `/client/notifications/{id}/read` | `notifications.read` | in-app feed backed by the license/API-key alert rows (`read-all` not built) |

## 5. Face Recognition API — `/api/v1/faces` (JWT **or** API key; permission/scope shown)

All write/billable endpoints run the pre-flight gate (client status → license → balance → rate limit → daily quota → settings limits) **before** the image is decoded.

| Method | Path | Scope / permission | Billable | Purpose |
|---|---|---|---|---|
| POST | `/faces/enroll` | `faces.enroll` | `Enroll` | multipart: `image`, `externalRef`, `displayName?`, `metadata?`, `consentReference` (required) → creates/extends a profile; returns `{profileId, templateId, quality, requestId, credits: {charged, remaining}}` |
| POST | `/faces/verify` | `faces.verify` | `Verify` | 1:1: `image` + `profileId` or `externalRef` → `{match, score, threshold, requestId, credits}` |
| POST | `/faces/identify` | `faces.identify` | `Identify` | 1:N: `image`, `topK?` → `{matches:[{profileId, externalRef, score}], bestScore, outcome, requestId, credits}` |
| POST | `/faces/detect` | `faces.detect` | `Detect` (default cost 0) | face count/quality only; nothing persisted |
| GET | `/faces/profiles` | `faces.read` | no | list/search profiles (by externalRef/status) |
| GET | `/faces/profiles/{id}` | `faces.read` | no | profile + template metadata (**never** returns embeddings) |
| PUT | `/faces/profiles/{id}` | `faces.manage` | no | update metadata/status |
| DELETE | `/faces/profiles/{id}` | `faces.erase` | no | irreversible erasure of templates + images; audited |
| DELETE | `/faces/profiles/{id}/templates/{templateId}` | `faces.erase` | no | remove one template |
| GET | `/faces/requests` | `faces.history` | no | recognition history (filter outcome/operation/date/profile) |
| GET | `/faces/requests/{id}` | `faces.history` | no | single request incl. match candidates & charge |
| GET | `/faces/balance` | `faces.read` | no | lightweight `{remaining, expiresAt, status}` for integrators |

Response for any billable call always includes the stable `outcome` string so integrators can branch without parsing messages.

Image rules (server-enforced, configurable): `JPEG/PNG/WebP` detected by magic bytes, ≤ 5 MB, ≤ 25 MP, re-encoded server-side (EXIF/metadata stripped), one request = one image.

## 6. Dashboard definitions (single source for API + UI)

| Metric | Definition | Source |
|---|---|---|
| Total / Active / Suspended clients | count by `Clients.Status` | Clients |
| Total / Available / Consumed licenses (credits) | Σ `TotalCredits` / Σ `RemainingCredits` (usable licenses) / Σ `ConsumedCredits`; also count of licenses by status | Licenses |
| Expiring licenses | `Active` with `ExpiresAt` within *N* days (default 30, configurable) | Licenses |
| Recognition requests | all rows of `FaceRecognitionRequests` in range | Requests / UsageLogs |
| Successful | `Outcome IN (Enrolled, Matched)` | |
| No match | `Outcome = NoMatch` (valid & billed answer) | |
| Failed | `Outcome IN (NoFaceDetected, MultipleFaces, LowQuality, ProviderError, Rejected)` | |
| Credits consumed trend | Σ `-Credits` of `Consume` rows per day | LicenseTransactions / UsageLogs |
| Client-wise usage | top-N clients by requests & credits in range | UsageLogs |
| API usage (client) | calls, error rate, p95 latency, by key | ApiRequestLogs/UsageLogs |
| System alerts | platform notifications (exhausted/expiring licenses, provider down, job failures, ledger-chain mismatch, error-rate spike) | Notifications + health checks |

## 7. Rate limiting & quotas
- **Layer 1 — anonymous/auth endpoints**: per-IP sliding window (e.g. 10/min login, 5/min forgot-password) + per-account lockout.
- **Layer 2 — authenticated**: partitioned per API key (or per user for JWT) — limit from key override → client setting → plan → platform default (all configurable). Token-bucket for face endpoints (burst allowed), fixed-window for the rest.
- **Layer 3 — daily quota per client**: counted in a distributed counter (in-memory v1, Redis when scaled out).
- Limits are read from configuration/settings only — no literals in code.

## 8. Webhooks (client-registered)
Events: `recognition.completed`, `license.low_balance`, `license.expiring`, `license.expired`, `license.exhausted`, `apikey.expiring`.
Delivery: `POST` JSON with headers `X-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, t + "." + body)>`, `X-Event-Id`, `X-Event-Type`; 5 s timeout; exponential-backoff retries (max 8) from the outbox; endpoint auto-disabled after sustained failure with a notification. URLs must be `https`, resolve to public IPs only (SSRF guard re-checked at send time, redirects not followed).

## 9. Documentation for integrators
Swagger UI (authorised, non-prod) + a rendered **"API Docs"** page in the client portal: quick-start, auth, cURL/C#/JS samples, error codes, rate limits, webhook verification snippet. Samples never contain real keys.
