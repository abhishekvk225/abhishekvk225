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
| 403 | `FORBIDDEN` · `LICENSE_SUSPENDED` · `CLIENT_SUSPENDED` · `CLIENT_INACTIVE` · `IP_NOT_ALLOWED` · `API_ACCESS_DISABLED` · `SELF_APPROVAL_FORBIDDEN` | `API_ACCESS_DISABLED`: the client's kill switch is on (all its API keys are refused). `SELF_APPROVAL_FORBIDDEN`: you cannot approve your own adjustment request. |
| 404 | `NOT_FOUND` | |
| 401 | `MFA_CODE_INVALID` · `MFA_CHALLENGE_INVALID` | Second sign-in step: wrong code / the challenge expired, was used or ran out of attempts (sign in again). |
| 409 | `CONFLICT` · `DUPLICATE_EXTERNAL_REF` · `CONCURRENCY_CONFLICT` · `LICENSE_INVALID_TRANSITION` · `APPROVAL_NOT_PENDING` · `APPROVAL_EXPIRED` · `MFA_ALREADY_ENABLED` · `MFA_NOT_ENABLED` · `MFA_NOT_PENDING` · `PROFILE_LIMIT_REACHED` · `APIKEY_LIMIT_REACHED` · `USER_LIMIT_REACHED` · `LAST_ADMIN` | |
| 413 | `PAYLOAD_TOO_LARGE` | |
| 422 | `NO_FACE_DETECTED` · `MULTIPLE_FACES` · `LOW_QUALITY_IMAGE` | Image processed, request not billable |
| 429 | `RATE_LIMITED` · `DAILY_QUOTA_EXCEEDED` | |
| 500 | `INTERNAL_ERROR` | Generic message + `correlationId` only |
| 502/503 | `FACE_PROVIDER_UNAVAILABLE` | Provider failure — **not billed**, `Retry-After` set |

## 2. Authentication & session — `/api/v1/auth` (anonymous unless noted; strictly rate-limited per IP + account)

| Method | Path | Purpose |
|---|---|---|
| POST | `/auth/login` | email + password → `{accessToken, expiresIn, refreshToken, user, mustChangePassword, mfaEnrolmentRequired}`; records LoginHistory; generic error on any failure. **When the account has two-factor authentication on** the response is instead `{mfaRequired: true, mfaChallengeToken, mfaChallengeExpiresIn: 300}` with empty tokens: finish with `/auth/mfa/verify` |
| POST | `/auth/mfa/verify` | `{challengeToken, code}` (6-digit authenticator code **or** a recovery code) → the normal token response. The challenge is single use, lives 5 minutes, is bound to the user (not to an IP) and dies after 5 codes; every wrong code also counts against the account lockout. A TOTP step already used is refused (replay). Strict per-IP rate limit |
| GET | `/auth/mfa` 🔒 | `{available, enabled, enrolmentRequired, enrolmentPending, recoveryCodesRemaining}` |
| POST | `/auth/mfa/enroll` 🔒 | starts (or restarts) enrolment: `{secretBase32, otpauthUri, issuer, accountName, algorithm: "SHA1", digits: 6, periodSeconds: 30}`. **The secret is shown once**; it is stored encrypted. 409 `MFA_ALREADY_ENABLED` once enabled |
| POST | `/auth/mfa/enroll/confirm` 🔒 | `{code}` → `{recoveryCodes (10, shown once), session}`; turns MFA on, ends earlier sessions and returns a fresh token pair. Five wrong codes discard the pending setup |
| POST | `/auth/mfa/recovery-codes` 🔒 | `{code}` (a live authenticator code) → `{recoveryCodes}`; the old codes stop working |
| POST | `/auth/refresh` | rotate refresh token (reuse detection revokes the family) |
| POST | `/auth/logout` 🔒 | revoke current refresh-token family |
| POST | `/auth/forgot-password` | always `202` (no account enumeration); emails a time-limited token |
| POST | `/auth/reset-password` | token + new password |
| POST | `/auth/change-password` 🔒 | current + new password; revokes other sessions |
| GET | `/auth/me` 🔒 | profile, role, permissions, client summary (name, status, timezone) |

JWT claims: `sub`, `jti`, `email`, `role`, `cid` (client id; absent for platform users), `sv` (security-stamp version), `iat/exp` (15 min), `mcp` (must change password) and `mer` (**must enrol in MFA**, present when policy requires a second factor the account does not have yet). A token carrying `mcp` or `mer` holds **no permissions**: only the `[Authorize]`-only endpoints (`/auth/change-password`, `/auth/mfa*`, `/auth/me`, `/auth/logout`) work. Signed with an asymmetric key (`kid` header, key rotation supported). Permissions are **not** in the token — resolved from a cached role→permission map so permission changes take effect without re-login.

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
| POST | `/admin/licenses/{id}/adjust` | `licenses.adjust` | ± credits with mandatory reason (`Adjustment` row). Up to `Licensing:MaxAdjustPerAction` (default 10 000 credits, up or down) it applies at once (200, the license). Above it **nothing is written**: it files a request and answers **202** with the request (`Location: /admin/license-adjustments/{id}`) |
| GET | `/admin/license-adjustments?status=&licenseId=` · `/{id}` | `licenses.read` | the approval queue (`Pending`, `Approved`, `Rejected`, `Expired`; a pending request past its deadline reads as `Expired`) |
| POST | `/admin/license-adjustments/{id}/approve` · `/reject` | `licenses.approve-adjust` | a **different** user decides within `Licensing:AdjustApprovalHours` (24 h). Approve applies the adjustment through the normal atomic path (license update + ledger row + audit in one transaction; the ledger actor is the approver). `reject` needs a reason. 403 `SELF_APPROVAL_FORBIDDEN`, 409 `APPROVAL_NOT_PENDING` (already decided / lost a race), 409 `APPROVAL_EXPIRED`. Every step is audited (`license.adjustment_requested/approved/rejected/expired`) |
| POST | `/admin/transactions/{id}/refund` | `licenses.adjust` | refund of a charge references the original transaction |
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
| POST | `/admin/licensing/verify-ledger` | `licenses.verify-ledger` | **non-blocking**: starts a check and answers **202** with `{id, status: "Running", ...}` (`Location: .../runs/{id}`); optional body `{licenseId}` limits it to one license (404 if unknown). 409 if a check is already running on any node (SQL application lock). Also runs nightly |
| GET | `/admin/licensing/verify-ledger/runs/{id}` · `/runs` | `licenses.verify-ledger` | status of a run (`Running`/`Completed`/`Failed`), counts and the first broken row of every failing license; the 20 latest runs. Checks the hash chain, balance **and the HMAC-signed checkpoints** (tail truncation, deleted rows, whole-license deletion) |
| GET | `/admin/licensing/ledger-breaks` | `licenses.verify-ledger` | the operator-visible alert list: ledger findings that are still open (`licenseId`, `clientId`, `breakKey`, `reason`, `firstSeenAt`, `lastSeenAt`, `timesSeen`, `alertedAt`, `lastReminderAt`), oldest first. Each break alerts once when first found (Critical log event 7001 + audit `ledger.verification_failed`), then only as a periodic reminder; a license that verifies clean closes its entries |
| POST | `/admin/users/{id}/mfa/reset` · `/admin/clients/{clientId}/users/{userId}/mfa/reset` | `users.mfa-reset` | **another Super Admin** switches a user's MFA off (`{reason}` mandatory, audited, the user's sessions end); 403 on yourself or a non-Super-Admin, 409 if not enabled |
| POST | `/admin/clients/{id}/api-keys/revoke-all` · `/api-keys/{keyId}/revoke` | `apikeys.emergency-revoke` | break-glass: revoke every active key of a client / one key (`{reason}` mandatory, audited). Effective on the serving node at once, on others within `ApiAuth:CacheSeconds` (5 s) |
| GET/PUT | `/admin/clients/{id}/api-access` | `apikeys.emergency-revoke` | the client-wide kill switch `{disabled, reason}`: while on, every API key of the client is refused with 403 `API_ACCESS_DISABLED`; portal sign-in is unaffected; keys are not modified, so switching it off restores access |
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
| GET | `/client/profile` · PUT | `client.profile.read` / `client.profile.update` (ClientAdmin) | company info (the platform's internal `notes` and the suspension reason text are never part of the client view) |
| GET | `/client/license` · `/client/licenses` · `/client/licenses/{id}/transactions` | `license.read` | balance, expiry, history (read-only) |
| GET | `/client/usage?from&to` | `usage.read` | detailed usage |
| GET/POST | `/client/users` · PUT `/{id}` · POST `/{id}/deactivate` · `/{id}/reset-password` | `users.manage` (ClientAdmin) | manage own users; cannot assign platform roles or roles with permissions the caller lacks; cannot remove the last admin; **can only change people with strictly less access than themselves** (never the account owner; equals only for the owner) — 403 otherwise. The `max users` cap is enforced atomically |
| GET/POST | `/client/api-keys` | `apikeys.read` / `apikeys.manage` | POST returns the raw key **once** (`key`, `prefix`) |
| POST | `/client/api-keys/{id}/regenerate` · `/revoke` | `apikeys.manage` | rotation / revocation |
| GET | `/client/api-logs` | `apilogs.read` | request log viewer (filter by key, status, date) |
| GET/PUT | `/client/settings/recognition` | `settings.recognition` | threshold, retention, quality, max faces (bounded by platform limits) |
| GET/PUT | `/client/settings/notifications` | `settings.notifications` | low-balance %, expiry reminder days, email on/off |
| GET/PUT | `/client/settings/security` | `settings.security` | password policy extras, require MFA, IP allow-list |
| GET/POST/PUT/DELETE | `/client/webhooks` · POST `/{id}/test` · `/{id}/rotate-secret` | `webhooks.manage` | endpoints (secret shown once) |
| GET | `/client/webhooks/{id}/deliveries` | `webhooks.manage` | delivery attempts |
| GET | `/client/audit-logs` | `audit.read.client` | own activity history. Rows written by **platform staff** show `actorType: "Platform"` with no `actorId` and no `ipAddress`: client views never expose who on the platform side did something |
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
- **Layer 3 — daily quota per client**: counted in a distributed counter.
- **Shared counters (M9b)**: layers 2 and 3 are counted in SQL Server (`api.UsageCounters`, one atomic upsert per block of permits) with an in-memory fast path, so the limits hold **across API nodes** (the sum of all nodes never exceeds the limit; a node that reserved permits and then went quiet loses them for that window, which is why small limits reserve one at a time). If the database is unreachable a node degrades to counting on its own and logs a warning. `Counters:*` in `deploy/CONFIG.md`.
- **Per-principal throttle (M9b)** on expensive or abusable actions, same shared counters, keyed by the signed-in user or API key (never an IP): dashboards (`Throttle:DashboardsPerMinute`, default 60), CSV exports (`ExportsPerMinute`, 6), webhook `POST .../test` (`WebhookTestsPerMinute`, 10) and `POST .../deliveries/{id}/retry` (`WebhookRetriesPerMinute`, 30). Over the limit: `429` `RATE_LIMITED` + `Retry-After`; the check runs after authentication/authorisation, so anonymous or forbidden calls never consume anyone's budget.
- Limits are read from configuration/settings only — no literals in code.

## 8. Webhooks (client-registered)
Events: `recognition.completed`, `license.low_balance`, `license.expiring`, `license.expired`, `license.exhausted`, `apikey.expiring`.
Delivery: `POST` JSON with headers `X-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, t + "." + body)>`, `X-Event-Id`, `X-Event-Type`; 5 s timeout; exponential-backoff retries (max 8) from the outbox; endpoint auto-disabled after sustained failure with a notification: **20 consecutive failed events** (an event counts once, when its first attempt fails; retries do not pile on; any success resets; test events never count). Scheduling is fair: at most `Webhooks:MaxPerEndpointPerCycle` (5) deliveries per endpoint in each 2-second cycle, `Webhooks:BatchSize` (50) in total, claimed by exactly one node. URLs must be `https`, resolve to public IPs only (SSRF guard re-checked at send time, redirects not followed).

## 9. Documentation for integrators
Swagger UI (authorised, non-prod) + a rendered **"API Docs"** page in the client portal: quick-start, auth, cURL/C#/JS samples, error codes, rate limits, webhook verification snippet. Samples never contain real keys.
