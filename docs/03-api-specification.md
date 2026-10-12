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
| GET | `/admin/email/outbox?status&template&recipient&from&to&page&pageSize` | `system.configure` | outgoing mail, newest first: `{id, to, subject, template, status, attempts, nextAttemptAt, lastAttemptAt, lastError, correlationId, createdAt, sentAt}` (bodies are never returned). `status` = Pending/Sending/Sent/Failed/Abandoned (Failed = permanently refused, Abandoned = transient failures used up every attempt), `recipient` = exact address or the start of one, `from`/`to` = UTC creation time. **`to` is masked (`j***@example.com`) unless the caller also holds `email.read`.** 400 `VALIDATION_FAILED` on an unknown status/template or inverted dates |
| POST | `/admin/email/outbox/{id}/retry` | `email.manage` | queues a Failed/Abandoned message again with a fresh set of attempts (audit `email.retried`); 409 when it is in another state or `EMAIL_BODY_PURGED`, 404 if unknown |
| GET | `/admin/email/suppressions` | `email.read` | addresses mail is no longer sent to (the server permanently refused them): `{id, address, reason, createdAt}`, paged |
| DELETE | `/admin/email/suppressions/{id}` | `email.manage` | clears one (audit `email.suppression_removed`); 204, 404 if unknown |
| POST | `/admin/email/test` | `email.manage` | `{to}` queues an `AdminTest` message and answers **202** `{id}`; rate limited per administrator (`RateLimiting:EmailTestPerMinute`, default 3 -> 429), audited (`email.test_sent`, address masked); 400 for a non-plain address. The result of the delivery shows in the outbox |
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

## 7a. Public API — `/api/v1/public` (anonymous; called server-to-server by the portal, no CORS)
Added in M11. All endpoints are `[AllowAnonymous]`, bodies are capped at 16 KiB, and each POST is also covered by the per-IP `RateLimiting:AuthPerIpPerMinute` limiter. No tenant data is ever returned.

| Method & path | Body | Success | Notes |
|---|---|---|---|
| `GET /public/plans` | – | `200` `[{id, name, description, credits, validityDays, highlights: string[], isTrial, displayPrice}]` | Active plans flagged public, by display order. `Cache-Control: public, max-age=60`. No prices: `displayPrice` is optional free text, otherwise show "Contact us". The trial plan reports `Signup:TrialCredits` / `Signup:TrialDays`. |
| `GET /public/config` | – | `200` `{signupEnabled, trialCredits, trialDays, captcha: {provider: "none"\|"turnstile", siteKey: string\|null}}` | The Turnstile secret is never exposed. |
| `POST /public/signup` | `{companyName, fullName, email, password, acceptTerms: true, captchaToken?, website?}` | `202` `{}` | Same response, body and (padded) timing whether the address is new, pending or already registered. New: a `PendingSignup` (password hash, SHA-256 of the token, 24 h expiry, one live row per address, a repeat replaces it) and a verification email linking to `{Portal:PublicBaseUrl}/verify-email?email=..&token=..`. Registered: a "you already have an account" email instead. Validation (password policy, `acceptTerms`, email format, disposable domains, missing fields) → `400` problem+json. `website` is a honeypot: non-empty = `202` and dropped. `403 SIGNUP_DISABLED` when `Signup:Enabled=false`; `400 CAPTCHA_FAILED`; `429 RATE_LIMITED` when the per-IP budget is used up. The per-address budget is exhausted silently (`202`, no email). |
| `POST /public/signup/resend` | `{email}` | `202` `{}` | Always accepted. Only when a live pending sign-up exists: issues a fresh token (the old link stops working), restarts the 24 h period, re-sends the email; at most 3 resends per sign-up. Same IP and per-address budgets as sign-up. |
| `POST /public/signup/verify` | `{email, token}` | `204` | Atomic and once-only: one conditional `UPDATE` claims the link, then in the same transaction the Client (Active, code from the company name, unique), the first `ClientAdmin` (password from the pending record, no forced change), the client data key and a trial license (`Signup:TrialCredits` credits, `Signup:TrialDays` days, ledger grant) are created through the same services as admin creation; audit `signup.verified` / `client.created` with source `public-signup`. Any failure (unknown, expired, wrong token, replay, address registered meanwhile, internal failure) is the same generic `400`; a failure rolls everything back and leaves the link usable. |
| `POST /public/contact` | `{name, email, company?, message (≤4000), website?}` | `202` `{}` | Stored as a `ContactRequest` (deleted after `Signup:ContactRetentionDays`, 180) and announced to `Signup:ContactNotifyEmail`. Honeypot as above. `429` after `Signup:MaxContactsPerIpPerHour`. |

Platform side: `GET /api/v1/admin/contact-requests?page&pageSize` (permission `clients.read`, newest first). Admin plan endpoints accept the optional `isPublic`, `isTrial`, `displayOrder`, `displayPrice`, `highlights` fields (omitted = keep the stored value; empty `displayPrice`/`highlights` clears it) and return them in `PlanDto`.

New error codes: `SIGNUP_DISABLED` (403), `CAPTCHA_FAILED` (400).

## 7b. Billing API (M12: buy credits online)

Hosted checkout only: the customer pays on the payment provider's page, **no card data ever reaches NexaVerify**. Prices are integers in minor units (paise, cents) of the pack's ISO 4217 currency. All client endpoints need a signed-in client **user** with `billing.read` (read) or `billing.manage` (checkout, cancel, edit the profile); both go to `ClientAdmin` by default and **API keys can never use them** (the permissions are not assignable to a key). While `Billing:Enabled=false` the client endpoints answer `404 BILLING_DISABLED`, the public list is `[]` and webhooks are 404. Every amount comes from the database (the pack, copied into the order as a snapshot); a request never carries a price. A redirect back from the provider is **not** proof of payment: only a verified webhook (or our own server-side query of the provider) marks an order paid. The providers are **unverified against the live APIs** (sandbox cannot call them): request shapes follow the providers' documentation and are covered by fixture/known-signature-vector tests.

### Client (`/api/v1/client/billing`, JWT, tenant from the credential)
| Method & path | Permission | Body / query | Success |
|---|---|---|---|
| `GET /config` | `billing.read` | - | `{enabled, taxPercent, taxLabel, currencies[], requireBillingProfile, requireTaxId}` (answered even when billing is off) |
| `GET /packs` | `billing.read` | - | `[{id, name, description, credits, validityDays, priceMinor, currency, taxPercent, taxLabel, taxMinor, totalMinor, highlights[]}]`, active packs in allowed currencies, display order |
| `POST /checkout` | `billing.manage` | `{packId, idempotencyKey?}` (letters, digits, `. _ : -`, max 100) | `201` `{orderId, checkoutUrl, expiresAt}` + `Location`. Same key + same pack returns the same open order (no second order); same key for another pack, or after the order finished: `409 CONFLICT`. `409 BILLING_PROFILE_INCOMPLETE` when `Billing:RequireBillingProfile` and the profile lacks name/address/city/postal/country/billing email (or tax id when `Billing:RequireTaxId`). `404` unknown/inactive/non-allowed-currency pack. `429 RATE_LIMITED` (per user and per client per hour, and too many open orders). `503 PAYMENT_PROVIDER_UNAVAILABLE` (the order is then marked Failed) |
| `GET /orders?page&pageSize&status` | `billing.read` | status: Pending, Paid, Failed, Expired, Cancelled, Refunded, PartiallyRefunded | paged `[{id, invoiceNumber?, packName, credits, totalMinor, currency, status, createdAt, paidAt}]` |
| `GET /orders/{id}` | `billing.read` | - | `{id, invoiceNumber?, packId, packName, credits, validityDays, subtotalMinor, taxMinor, totalMinor, taxPercent, taxLabel, currency, status, createdAt, paidAt?, expiresAt, licenseId?, refundedMinor}`. **Poll target of the success page.** A Pending order whose webhook is late is confirmed with the provider first (at most once per `Billing:InlineReconcileSeconds`); `licenseId` appears when paid. Another client's id: `404` |
| `POST /orders/{id}/cancel` | `billing.manage` | - | the order (Cancelled); `409` unless Pending |
| `GET /orders/{id}/invoice` | `billing.read` | - | `text/html` printable invoice (seller from `Billing:Seller:*`, buyer snapshot from checkout time, subtotal, tax line, total, refunds). `409` until paid. All values HTML-encoded; own strict CSP |
| `GET /orders/export.csv` | `billing.read` | - | `text/csv` (newest 10 000 orders, formula-injection-safe, audited, throttled like other exports) |
| `GET/PUT /profile` | `billing.read` / `billing.manage` | `{legalName, addressLine1, addressLine2?, city, state?, postalCode, country (ISO 3166-1 alpha-2), taxId? (GSTIN/VAT), billingEmail}` | `{...same, isComplete}`; tax id normalised (upper case, spaces/hyphens removed) and never audited |

### Public
`GET /api/v1/public/packs` (anonymous, `Cache-Control: public, max-age=60`): `[{id, name, description, credits, validityDays, priceMinor, currency, taxPercent, taxLabel, totalMinor, highlights[]}]`, only active + public packs in allowed currencies; `[]` while billing is off.

### Platform (`/api/v1/admin/billing`, staff)
| Method & path | Permission | Notes |
|---|---|---|
| `GET /config` | `billing.orders.read` | `{enabled, provider, mode: sandbox\|live\|simulated\|none, taxPercent, taxLabel, currencies[], requireBillingProfile}`. The sandbox/live mode is shown to staff only; never any key |
| `GET/POST /packs`, `GET/PUT/DELETE /packs/{id}` | `billing.packs.manage` | `{name, description?, credits, validityDays, priceMinor, currency, highlights[]?, displayOrder, isActive, isPublic}`; editing a price never changes existing orders (snapshot); `DELETE` is `409` once the pack has orders (switch it off instead). Audited (`billing.pack_*`) |
| `GET /orders?clientId&status&from&to&page&pageSize` | `billing.orders.read` | paged `[{id, clientId, clientName, invoiceNumber?, packName, credits, totalMinor, refundedMinor, currency, status, provider, createdAt, paidAt}]` |
| `GET /orders/{id}` | `billing.orders.read` | `{order, clientId, clientName, provider, providerSessionId, providerPaymentId, creditsRevoked, failureReason, refunds[{id, amountMinor, creditsRevoked, reason, status, providerRefundId, createdBy, createdAt}]}` |
| `POST /orders/{id}/refund` | `billing.refund` | `{amountMinor?, reason}` (omitted amount = everything left). The amount is **reserved with one conditional UPDATE before** the provider is called (parallel refunds cannot exceed the total), the provider refund uses the refund id as idempotency key, then the **unused** credits of the top-up license are taken back in proportion (cumulative, rounded once; never below credits already consumed, never negative) as an `Adjustment` ledger row; audit `billing.refund`; webhook `license.credits_revoked`; email to the buyer. `409 ORDER_NOT_REFUNDABLE` unless Paid/PartiallyRefunded. `503` if the provider refuses (nothing changed) |
| `POST /orders/{id}/reconcile` | `billing.refund` | asks the provider and applies the answer (idempotent with webhooks) |

### Webhook and development endpoints
* `POST /api/v1/billing/webhooks/{provider}` (`stripe` or `razorpay`; only the configured provider exists, others `404`). Anonymous; body cap `Billing:WebhookMaxBodyBytes` (413). The HMAC-SHA256 signature over the **raw body** is verified in constant time **before** the body is parsed or the database touched (Stripe: `Stripe-Signature` with `t=` timestamp, tolerance `Billing:Stripe:ToleranceSeconds` = 5 min; Razorpay: `X-Razorpay-Signature`, replay protection by event id). Bad/missing signature or unreadable body: `400`. Otherwise `200 {"received":true}` for every outcome, including duplicates and anomalies. Idempotent through the unique `(provider, eventId)` ledger `billing.PaymentEvents`.
* On `PaymentSucceeded`, only if amount and currency equal the order snapshot and the order is Pending/Expired/Failed, in **one transaction**: event row, order to Paid (single conditional UPDATE: one winner among parallel deliveries/reconciles), invoice number `INV-YYYY-000001` (gapless per year), license `Top-up: <pack>` through the normal license service (credits, expiry = now + validityDays, ledger `Grant` referencing the order), audit `billing.payment_succeeded`, in-app notification, outbound webhook `license.topped_up`. After commit: receipt email with the order link. A mismatch, unknown order, other provider, cancelled order or a second payment id is recorded as an anomaly (`AnomalyAmountMismatch`, `AnomalyOrderNotFound`, `AnomalyNotPayable`, `AnomalyDuplicatePayment`, ...), logged at Error (event 9010) and **not** granted. `PaymentFailed` / `CheckoutExpired` only update the status; a refund created at the provider outside NexaVerify is flagged (`AnomalyExternalRefund`).
* `POST /api/v1/dev/billing/simulate/{orderId}` `{outcome: success|failure|expired}` exists **only** in Development/Testing hosts (the controller is removed elsewhere) and only with `Billing:Provider=Simulated`, which the host refuses to start with outside those environments. Needs `billing.manage` and the caller's own order. The simulated checkout URL is `{Portal:PublicBaseUrl}/dev/pay/{orderId}`.
* Background job (`Billing:JobIntervalMinutes`): Pending orders older than `Billing:ReconcileAfterMinutes` (15) are confirmed with the provider; unpaid after `Billing:PendingExpiryHours` (24) they are closed at the provider and marked Expired. A payment arriving later for an Expired/Failed order is still honoured.
* Existing alerts: `license.low_balance`, `license.exhausted`, `license.expiring` and `license.expired` notifications and webhook payloads gain `billingUrl` (and the link in the text) while billing is enabled.
* New webhook events (subscribable): `license.topped_up`, `license.credits_revoked`. New error codes: `BILLING_DISABLED` (404), `BILLING_PROFILE_INCOMPLETE` (409), `PAYMENT_PROVIDER_UNAVAILABLE` (503), `ORDER_NOT_REFUNDABLE` (409). Audit actions: `billing.checkout_created`, `billing.payment_succeeded`, `billing.payment_anomaly`, `billing.order_cancelled`, `billing.order_expired`, `billing.order_reconciled`, `billing.refund_requested`, `billing.refund`, `billing.refund_failed`, `billing.profile_updated`, `billing.pack_created|updated|deleted`. Log events 9001-9018.

### 7c. Email delivery (M13)
* Every mail is rendered from a template (HTML + plain text, one source) and stored in `tenancy.OutboundEmails` **in the same transaction as the business change** that caused it; a background dispatcher (any number of nodes, `UPDLOCK, READPAST` claim under a lease) sends it, retries transient failures with exponential backoff and jitter, ends permanent ones (a refused recipient is suppressed, `tenancy.EmailSuppressions`) and abandons a message after `Email:Dispatcher:MaxAttempts` (Critical log event 8323, visible in the outbox). Bodies that carry a one-time token are erased the moment the mail is sent; finished messages are deleted after 30 days. Log events 8310 (dropped recipient), 8320-8330 (dispatcher), 8301/8302 (logging provider).
* Templates: `VerifyEmail`, `AlreadyRegistered`, `PasswordReset`, `PasswordChanged`, `UserInvite`, `MfaEnabled`, `MfaDisabled`, `MfaRecoveryCodeUsed`, `LicenseLowBalance`, `LicenseExpiring`, `PaymentReceipt`, `RefundNotice`, `ContactRequest`, `WebhookEndpointDisabled`, `AdminTest`. Links are built only under `Email:PublicBaseUrl` (fallback `Portal:PublicBaseUrl`). Audit actions: `email.retried`, `email.suppression_removed`, `email.test_sent`.
* The payment receipt and refund notice now link to `/client/billing/orders/{id}` (the portal's real route; before they pointed at `/billing/orders/{id}`).

### 7d. Document verification (M14, built — Backend-A round two; routes below are implemented and covered by API integration tests)
Design and rationale: `07-document-verification.md` (§18 lists where the build differs). Processing is **asynchronous**: submit returns `202` with a request id; poll `status`/`result` or subscribe to the webhook `verification.completed`. Client routes accept JWT **or** API key (tenant from the credential, never in the request). Other tenants' ids return 404. Nothing is reported as "verified" unless `external.state = Verified` (a named provider answered); `ProviderNotConfigured` is the release-1 default for PAN and passport.

**Client (`/api/v1/verifications`)**
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/verifications/modules` | `verification.read` | Catalogue incl. Upcoming modules (`available:false`, no endpoints, no credits), effective status, upload limits, provider configured flag |
| POST | `/verifications/bank-statements` | `verification.bank-statement.submit` | multipart `document` + `externalRef?`, `consentReference`, `accountHolderName?`, `bankName?`, `accountNumberLast4?`, `periodFrom?`, `periodTo?`; `Idempotency-Key` optional; `202` (replay `200`); `Location` -> status |
| POST | `/verifications/pan` | `verification.pan.submit` | multipart `document?` + `panNumber?` (one of them required), `fullName`, `consentReference`, `dateOfBirth?`, `fatherName?`, `externalRef?` |
| POST | `/verifications/passports` | `verification.passport.submit` | multipart `document` (required) + `consentReference`, `passportNumber?`, `fullName?`, `dateOfBirth?`, `nationality?`, `dateOfIssue?`, `dateOfExpiry?`, `externalRef?` |
| GET | `/verifications` | `verification.read` | filters `type,status,reviewRequired,externalState,from,to,search`; paging as §1 |
| GET | `/verifications/{id}` | `verification.read` | result: stages, assurance, checks, review reasons, field matches, extracted (masked identifiers), external block, credits (only with `license.read`) |
| GET | `/verifications/{id}/status` | `verification.read` | lightweight polling |
| GET | `/verifications/{id}/transactions` | `verification.read` | bank statement transactions, `pageSize` <= 500 |
| POST | `/verifications/{id}/cancel` | `verification.read` + the submit permission of the request's type (checked by the service) | `Queued`: cancelled at once and its data erased. `Processing`: asked to stop, the worker ends it between stages. Otherwise 409 `VERIFICATION_NOT_CANCELLABLE`. Returns the status |
| POST | `/verifications/{id}/retry-external` | `verification.read` + the submit permission of the type; `[Throttle(VerificationSubmit)]` | re-run the provider stage only (finished request, retryable `external.state`, provider configured now, data not erased); the request is **re-queued** and a worker claims it under a lease; `202` + `Location`; 409 `VERIFICATION_NOT_RETRYABLE` |
| DELETE | `/verifications/{id}/data` | `verification.erase` | immediate purge of document + result + checks; `204`; 409 while `Queued`/`Processing` (cancel first); audited `verification.erased` |
| POST | `/verifications/{id}/downloads` | `verification.report.download` (`verification.document.download` additionally for the original, which is also off unless `Verification:Download:AllowOriginalDownload`) | `{kind: Report\|TransactionsCsv\|OriginalDocument, format?: json\|html}` -> `{downloadUrl, expiresAt}`; `downloadUrl` is a relative path, valid `Verification:Download:LinkSeconds` (60 s), single use, bound to the caller; `[Throttle(VerificationDownload)]`. A report needs a finished request (409 otherwise), the CSV a finished bank statement (404 otherwise) |
| GET | `/verifications/downloads/{token}` | `verification.read` at the route; the service requires `verification.report.download` (or `…document.download` for the original) | authenticated, bound to caller/tenant/request, consumed with one conditional UPDATE; `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, `Content-Disposition: attachment`, CSP `default-src 'none'`; unknown, foreign or other-principal token: 404, expired/used: `410 DOWNLOAD_LINK_EXPIRED`; audited `verification.report_downloaded` / `verification.document_downloaded` |

**Platform (`/api/v1/admin`)**
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/admin/verifications/modules` | `verification.admin.read` | availability, provider configuration status (no secrets), last-24 h provider outcomes, resolved credit cost per operation |
| PUT | `/admin/verifications/modules/{key}` | `verification.admin.configure` | `{isEnabled, reason, rowVersion}` (`rowVersion` = the Base64 string of the module list; stale -> 409 `CONCURRENCY_CONFLICT`); reason required; audited `verification.module_enabled|disabled` with the reason; 404 for Upcoming/unknown keys; a disabled module answers submissions with 404 `VERIFICATION_MODULE_DISABLED`, already accepted requests still finish |
| GET | `/admin/verifications/stats` (+ `/stats/export.csv`) | `verification.admin.read` | volumes, success/failure/review, provider errors, credits, latency, queue depth |
| GET | `/admin/clients/{clientId}/verifications` · `/{id}` | `verification.admin.read` | metadata only (no documents, extracted data, masked identifiers or checks); each detail view audited |
| GET | `/admin/verifications/audit` | `audit.read` | platform audit rows with action `verification.*` (module switches, administrative views, stats exports); `from,to,page,pageSize` |
| (existing) | `/admin/cost-rules…` | `licenses.cost-rules` | credit cost per operation: `BankStatementExtraction`, `PanExtraction`, `PanProviderCheck`, `PassportExtraction`, `PassportProviderCheck` |

* Billing (as built, `07` §8): charged per stage **on completion**, in the same transaction that stores the stage result, through the existing ledger (idempotency keys `vr:{requestId:N}:x` extraction, `:p` provider; the ledger row's `RecognitionRequestId` is the verification request id). Extraction is charged when a pipeline result is stored (a manual-review outcome is charged); the provider stage only for a definitive answer (`Verified`, `NotVerified`); failed, unreadable, cancelled, expired, credit-starved requests and every provider failure (`ProviderError`, `Timeout`, `Inconclusive`, `ProviderNotConfigured`) consume nothing. Pre-flight at submit (`402 LICENSE_*`) covers extraction plus the provider stage when a provider is configured; the worker pre-flights again and fails a request with `CREDITS_UNAVAILABLE` (no data kept) if the credits were taken meanwhile. A PAN submitted as a number only is still charged the extraction price for its local checks.
* Credit figures in a result (`credits`) appear only for callers that also hold `license.read`.
* API keys: `verification.bank-statement.submit`, `verification.pan.submit`, `verification.passport.submit`, `verification.read`, `verification.report.download`, `verification.erase` are assignable scopes; the original-document download and the platform keys are not. State-changing verification calls of portal users (POST/DELETE) count against the per-user rate limit and daily quota like face calls; polling (GET) does not; API keys are metered on every call.
* Upload rules (configurable, `Verification:Upload:*`): PDF/JPEG/PNG/WebP detected by magic bytes (extension and MIME must agree), default 10 MB (the route's body limit is `MaxFileBytes + RequestOverheadBytes`, set per route from configuration; the exact file limit answers `413 DOCUMENT_TOO_LARGE`), page caps 30/2/4, encrypted or active-content (JavaScript, launch, embedded files) PDFs rejected with `422`. An empty file part counts as no file. Nothing is stored or charged for a rejected upload.
* Accepted submit: `202` with `Location` (status URL) and `Retry-After`; a replay of the same `Idempotency-Key` + document + fields answers `200` with `replayed: true` (the key is bound to client, type, document hash and fields on the server, max 100 characters).
* New error codes: `DOCUMENT_REQUIRED`, `DOCUMENT_INVALID`, `DOCUMENT_UNSUPPORTED_TYPE` (400); `DOCUMENT_TOO_LARGE` (413); `DOCUMENT_TOO_MANY_PAGES`, `DOCUMENT_ENCRYPTED`, `DOCUMENT_ACTIVE_CONTENT` (422, found at inspection); `VERIFICATION_MODULE_DISABLED` (404); `VERIFICATION_NOT_CANCELLABLE`, `VERIFICATION_NOT_RETRYABLE` (409); `DOWNLOAD_LINK_EXPIRED` (410); `VERIFICATION_QUEUE_FULL` (429); `PROCESSING_UNAVAILABLE` (503). Provider outcomes are not errors (`200` with `external.state`).
* Rate limits: per-principal throttles `Throttle:VerificationSubmitsPerMinute` (20) and `Throttle:VerificationDownloadsPerMinute` (30); `Verification:Limits:MaxQueuedPerClient` (50).
* Webhook `verification.completed` is published inside the transaction that ends the request (completed, review required, failed, cancelled): `{requestId, type, status, reviewRequired, externalVerification, assurance, creditsCharged, balanceAfter}`, no personal data. Requests ended by the lease sweeper (`PROCESSING_TIMEOUT`) or the expiry sweep (`EXPIRED`) are ended by a set-based statement and do not publish it (open item). New permission keys are listed in `04` §14.

## 7. Rate limiting & quotas
- **Layer 1 — anonymous/auth endpoints**: per-IP sliding window (e.g. 10/min login, 5/min forgot-password) + per-account lockout.
- **Layer 2 — authenticated**: partitioned per API key (or per user for JWT) — limit from key override → client setting → plan → platform default (all configurable). Token-bucket for face endpoints (burst allowed), fixed-window for the rest.
- **Layer 3 — daily quota per client**: counted in a distributed counter.
- **Shared counters (M9b)**: layers 2 and 3 are counted in SQL Server (`api.UsageCounters`, one atomic upsert per block of permits) with an in-memory fast path, so the limits hold **across API nodes** (the sum of all nodes never exceeds the limit; a node that reserved permits and then went quiet loses them for that window, which is why small limits reserve one at a time). If the database is unreachable a node degrades to counting on its own and logs a warning. `Counters:*` in `deploy/CONFIG.md`.
- **Per-principal throttle (M9b)** on expensive or abusable actions, same shared counters, keyed by the signed-in user or API key (never an IP): dashboards (`Throttle:DashboardsPerMinute`, default 60), CSV exports (`ExportsPerMinute`, 6), webhook `POST .../test` (`WebhookTestsPerMinute`, 10) and `POST .../deliveries/{id}/retry` (`WebhookRetriesPerMinute`, 30). Over the limit: `429` `RATE_LIMITED` + `Retry-After`; the check runs after authentication/authorisation, so anonymous or forbidden calls never consume anyone's budget.
- Limits are read from configuration/settings only — no literals in code.

## 8. Webhooks (client-registered)
Events: `recognition.completed`, `license.low_balance`, `license.expiring`, `license.expired`, `license.exhausted`, `apikey.expiring` (M14: `verification.completed`, §7d).
Delivery: `POST` JSON with headers `X-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, t + "." + body)>`, `X-Event-Id`, `X-Event-Type`; 5 s timeout; exponential-backoff retries (max 8) from the outbox; endpoint auto-disabled after sustained failure with a notification: **20 consecutive failed events** (an event counts once, when its first attempt fails; retries do not pile on; any success resets; test events never count). Scheduling is fair: at most `Webhooks:MaxPerEndpointPerCycle` (5) deliveries per endpoint in each 2-second cycle, `Webhooks:BatchSize` (50) in total, claimed by exactly one node. URLs must be `https`, resolve to public IPs only (SSRF guard re-checked at send time, redirects not followed).

## 9. Documentation for integrators
Swagger UI (authorised, non-prod) + a rendered **"API Docs"** page in the client portal: quick-start, auth, cURL/C#/JS samples, error codes, rate limits, webhook verification snippet. Samples never contain real keys.
