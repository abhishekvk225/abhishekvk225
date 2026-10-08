# Security review: M2 re-verification, M3 Client Management, M4 Licensing & Metering

Reviewer: Security Agent (independent). Baseline: `docs/04-security-strategy.md`. Date: 2026-10-08. Commit reviewed: `ef1c252`.
Method: static review of the code paths listed below, with attack-path reasoning against the checklist. No product code was changed.

## Verdicts

| Module | Verdict | Blocking issues |
|---|---|---|
| M2 Identity & Access (re-verification of fixes) | **PASS-WITH-CONDITIONS** | None Critical/High. All five claimed fixes are effective in code. Conditions: M2-L1..L4 |
| M3 Client Management | **PASS-WITH-CONDITIONS** | None Critical/High. Fix M3-M1..M3 before the client portal ships (M8) |
| M4 Licensing & Metering | **PASS-WITH-CONDITIONS** | None Critical/High today. M4-M1 and M4-M2 are **hard prerequisites for M5**, the first caller of `ChargeAsync`. Without them they become High |

No Critical or High finding was demonstrated, so nothing is FAIL. The M4 verdict assumes the conditions are closed (or explicitly designed in) when M5 wires metering into the API.

## Tools run / not run
- `dotnet list NexaVerify.slnx package --vulnerable --include-transitive`: ran, **no vulnerable packages** in any project.
- CI: `ci.yml` has the vulnerable-package gate and `gitleaks-action`; `codeql.yml` and `dependency-review.yml` exist. Not executed here (need GitHub).
- gitleaks binary: not installed locally, **not run**. Manual grep for `AllowEphemeralKey`/`MasterKeyBase64`/`SigningKeyPem` found no committed secrets (only the dev switches noted in M2-L4).
- Raw SQL grep: `FromSql`/`ExecuteSql*`/`SqlQueryRaw`/`CreateCommand` appear only in `Infrastructure/Platform` (`MeteringStore`, `LoginThrottle`), the guard/RLS installers, the health check and `DatabaseInitializer`. All are parameterised or built from the EF model with bracket quoting. No SQL injection found. `IgnoreQueryFilters`: no occurrences in `src`.
- SQL Server integration test suites (need a running container): **not run** in this review. The existing tests `MeteringTests`, `SecurityHardeningTests`, `AuthFlowTests` and the RLS tests were read for coverage only.

---

## Findings summary (ranked)

| ID | Sev | Module | Title |
|---|---|---|---|
| M4-M1 | Medium (High once M5 exposes an idempotency key) | M4 | Idempotent replay is not bound to operation or request: a reused key skips billing |
| M4-M2 | Medium (Potential) | M4 | Preflight and charge are not atomic: concurrent requests can obtain unpaid service on a near-empty balance |
| M4-M3 | Medium | M4 | Hash chain is unkeyed and unanchored, serialisation is not enforced by the database, and verification is on-demand only |
| M4-M4 | Medium | M4/M3 | A client sees internal data: license `Notes`, ledger actor ids and reasons, and all of this for the low-privilege `ClientUser` |
| M3-M1 | Medium | M3 | Platform staff IP addresses and ids are exposed to client admins via audit logs |
| M3-M2 | Medium | M3 | Platform-internal `Client.Notes` and `StatusReason` are returned to the client |
| M3-M3 | Medium | M3 | Delegated client user manager can demote or deactivate a more privileged user (M2 H-2 pattern not applied to client users) |
| M4-M5 | Medium | M4 | Single `licenses.adjust` permission mints credits with no cap or second approver |
| M4-L1 | Low | M4 | `ExpiringInDays` unbounded: HTTP 500 via `DateTime.AddDays` |
| M4-L2 | Low | M4 | `DateTime.SpecifyKind(..., Utc)` relabels instead of converting |
| M4-L3 | Low | M4 | `int` overflow in credit sums and totals |
| M4-L4 | Low | M4 | Admin kill-switch can lose to optimistic concurrency under load |
| M4-L5 | Low | M4 | Raw consume SQL bypasses the per-command session-context re-apply |
| M4-L6 | Low | M4 | Idempotency key has no length or charset validation |
| M3-L1 | Low | M3 | Cross-tenant e-mail existence oracle when inviting users |
| M3-L2 | Low | M3 | Settings: weak allow-list and origin validation; retention limit not capped by platform |
| M3-L3 | Low | M3 | Per-client DEK cached unwrapped for 10 min per instance: crypto-shred is not immediate; KEK rotation not implementable as written |
| M3-L4 | Low | M3 | TOCTOU on MaxUsers and last-admin guards |
| M2-L1 | Low | M2 | Refresh claim is not in the same transaction as the successor insert; logout races with refresh |
| M2-L2 | Low | M2 | Readiness privilege check misses `db_ddladmin` and similar; RLS is not a barrier against a compromised app |
| M2-L3 | Low | M2 | Login timing: residual difference between unknown and known user |
| M2-L4 | Low | M2 | Production-only guard on dev switches; ephemeral keys committed in Migrator config |
| M3-I1 / M4-I1 | Info | | See the end of the report |

No Critical findings. No High findings.

---

## M4: Licensing & Metering

### What was verified as sound
- **Atomic deduction**: `MeteringStore.TryConsumeAsync` (`src/Infrastructure/Platform/MeteringStore.cs:37-71`) is a single conditional `UPDATE ... WHERE ClientId = @client AND Status='Active' AND StartsAt<=@now AND ExpiresAt>@now AND Total-Consumed>=@cost ... OUTPUT`, parameterised, and enlisted in the caller's transaction. Concurrent charges cannot overdraw. The DB also enforces `CK_Licenses_Credits` (`LicensingConfigurations.cs:29`) and `CK_LicenseTransactions_Balance` (`:71`) as a backstop.
- **Per-license serialisation of the ledger tail**: every ledger append is preceded by a statement that updates the license row (consume UPDATE; `SaveChanges` before `AppendAsync` in revoke/renew/adjust/refund/expire), so the row lock serialises `GetTailHashAsync` + insert. I traced each caller (`LicenseService.cs:150, 207, 231, 251, 289`; `LicenseMeteringService.cs:144`; `PlanAndCostServices.cs:369`). Stale in-memory `before` values in admin paths are protected by the rowversion token (a concurrent consume bumps it, which yields a 409 rather than a wrong ledger row).
- **Append-only**: `LicenseTransaction : IAppendOnly` yields an `INSTEAD OF UPDATE, DELETE` trigger (`AppendOnlyTriggerBuilder.cs`), a `DENY UPDATE, DELETE` for the app principal (`DatabaseInitializer.cs:108-114`), and readiness fails if the trigger is missing (`TenantProtectionHealthCheck.cs:45-52,69-78`).
- **Tenant isolation**: `License`, `LicenseTransaction`, `ClientCostRule` are `ITenantOwned` (query filter, write guard, RLS via the model-driven installer). Client endpoints (`ClientLicenseController`) are read-only, take no client id, and check `ClientId == _currentUser.ClientId` on top of the filter (`PlanAndCostServices.cs:300-317`) so cross-tenant ids return 404. No client-scope endpoint can create, adjust, renew or refund. There is no way for a client to grant itself credits through the API (all mutations need Platform-scope permissions, and `PermissionAuthorizationHandler` enforces scope, `PermissionAuthorization.cs:97-104`).
- **Authorization map**: every action in `LicensesController`, `PlansController`, `CostRulesController` and `ClientLicenseController` carries an explicit `[HasPermission]`. Reads use `licenses.read`; status changes use `licenses.manage-status`; renew uses `licenses.renew`; adjust and refund use `licenses.adjust`; cost-rule writes use `licenses.cost-rules`; plan writes use `plans.manage`. Sensible split. Clients cannot be addressed by body (`clientId` is a route value, request records have no `ClientId`), so no mass-assignment. `IsSystem` client is excluded from license/cost-rule creation.
- **Refund**: cannot exceed consumed credits (`License.cs:218`); the unique filtered index on `(ReferenceTransactionId) WHERE Type='Refund'` (`LicensingConfigurations.cs:87`) stops double refunds under a race; only `Consume` rows can be refunded.
- **Idempotency scoping**: unique `(ClientId, IdempotencyKey)` (`LicensingConfigurations.cs:86`), so one tenant's key cannot collide with or replay another tenant's. The racing duplicate rolls back its deduction with its transaction (`LicenseMeteringService.cs:110-115`).
- **Input validation**: validators exist for all license/plan/cost-rule request records with bounds (credits 0..100M, reason required and capped at 500). `SetCostRule` parses enums with `Enum.IsDefined`. The page size is clamped.
- **Expiry**: usability is decided from timestamps at use time (consume SQL and `IsUsable`); the sweeper only does housekeeping, per license, in a platform scope with a reason.

### Findings

**M4-M1 (Medium; becomes High if M5 accepts a client-supplied key). Idempotent replay is not bound to the operation or request.**
`src/Application/Licensing/LicenseMeteringService.cs:101-104`, `:161-162`; index `LicensingConfigurations.cs:86`.
`ChargeAsync` returns the stored ledger row for `(ClientId, IdempotencyKey)` without checking that the replay is the *same* operation, outcome or `RecognitionRequestId`.
Attack (once M5 forwards an `Idempotency-Key`/request header into `ChargeCommand.IdempotencyKey`): a client sends `Identify` with key `K` and is charged once. It then sends every later paid request (Enroll, Verify, Identify...) with the same `K`: each takes the replay branch, `Charged` is non-zero and `Replayed=true`, but nothing is deducted. Unlimited free service. A key is also not required to be unique per request by anything in the service.
Fix: (a) derive the key server-side from the request id (`RecognitionRequestId`) and never accept an arbitrary client string; or (b) store `Operation` and `RecognitionRequestId` (and a hash of the request body) in the row and return `409 IDEMPOTENCY_KEY_REUSED` when a replay differs; (c) in M5, a replay must not return a successful *result* for a different payload. Add a test: same key, different operation yields conflict and no free usage.

**M4-M2 (Medium, Potential). Preflight and charge are separate; unpaid service is possible at the boundary.**
`LicenseMeteringService.cs:67-95` (preflight, no reservation) vs `:118-159` (charge). `ChargeAsync` can return `402 INSUFFICIENT_BALANCE` after the work was done.
Scenario: balance = 1 credit. The attacker fires N parallel Identify calls. All N pass preflight; N images are processed; only one charge succeeds. If M5 returns the result when the charge fails (or ignores the charge error), the client got N-1 operations free. Also a DoS lever against the face engine at zero cost.
Confirm: needs the M5 code. Fix: charge-before-compute with refund-on-failure (reserve then confirm), or withhold the result and return 402 when the post-work charge fails, never ignoring the `Result`. Document the contract for M5 and add a concurrency test with balance = 1.

**M4-M3 (Medium). Ledger tamper-resistance gaps (hash-chain).**
`src/Domain/Licensing/LicenseTransaction.cs:101-123`, `LicenseService.cs:408-450`, `LicensingConfigurations.cs:84`, `LicensingRepositories.cs:102-104`.
1. The chain is plain SHA-256 with no secret. Anyone who can bypass the trigger (db_owner, restored backup, DBA, or an attacker with DDL) can rewrite rows *and recompute the entire chain*, so `verify-ledger` still reports valid. `Licenses` is mutable by design, so Total/Consumed can be edited to match.
2. No external anchor. Deleting the *newest* rows (needs trigger disable) is only caught if the license balance disagrees, which the same attacker also edits.
3. Correctness of "one writer per license at a time" relies on a code convention (`LedgerWriter` is public; its comment says "must be called inside the transaction that already changed the license row"). Nothing at the database prevents two rows with the same `PrevHash` (a fork). Add a unique index on `(LicenseId, PrevHash)`.
4. Verification is only an on-demand admin call. `docs/04` T9/§10 promise a nightly verification and a chain-mismatch alert; no job exists (grep: `VerifyLedgerAsync` is only used by the controller). `LedgerVerifier` also loads the whole ledger in memory (`GetAllAsync`), unbounded.
5. The hash does not cover `Id` or `CorrelationId` (Low).
Fix: use HMAC-SHA256 with a key held outside the DB (the master-key provider), publish a signed checkpoint (license id, last id, last hash) periodically to external storage or the audit sink, add the unique `(LicenseId, PrevHash)` index, add a nightly reconciliation job (chain plus `SUM(Credits)` against license Remaining) that raises the alert, and stream verification in pages.

**M4-M4 (Medium). Client-visible license and ledger responses expose internal data and exceed the documented client-user view.**
`PlanAndCostServices.cs:300-317` returns `LicenseDto` (includes `Notes`, `SuspendedReason`, `LicenseKey`; `LicenseSupport.cs:65-72`) and the raw ledger (`LicenseTransactionDto`: `Reason`, `ActorType`, `ActorId`, `LicenseSupport.cs:82-84`). `Permissions.cs:192-202` gives the **ClientUser** role `license.read`, while `docs/04` §3 says Client User sees "balance/expiry only".
Scenario: any low-privilege client user calls `GET /api/v1/client/licenses/{id}` and reads the platform operator's internal `Notes` (set by staff via `PUT /admin/licenses/{id}`), and `GET .../transactions` to read adjustment reasons ("goodwill credit after complaint X"), and the user GUIDs of platform staff who did the adjustment (`ActorId` for platform staff).
Fix: a dedicated client DTO without `Notes`, `ActorId` and free-text `Reason` (map to a controlled label); split permissions (`license.read` summary for ClientUser vs `license.ledger.read` for ClientAdmin).

**M4-M5 (Medium). No cap or second approver on credit minting.**
`LicenseService.cs:238-254`, `LicensingControllers.cs:73-76`; also refund `:88-91` and renew `:68-71`.
One compromised or malicious staff account with `licenses.adjust` can add up to 100,000,000 credits per call, unlimited calls, to any client. It is audited and ledgered (good for detection) but not preventable or alerted.
Fix: separate positive-adjust from negative-adjust permission, per-actor daily cap, dual approval above a threshold, alert on large adjustments (docs/04 §10 "unusual consumption").

**M4-L1 (Low).** `LicenseListQuery.ExpiringInDays` has no validator (`LicenseDtos.cs:45`, `LicensingRepositories.cs:45-48`). `now.AddDays(int.MaxValue)` throws `ArgumentOutOfRangeException`, so an authenticated admin gets a 500 (error-handler path, no leak seen, but avoidable). Add a validator (0..3650) for the query record. Same for unvalidated `Search` length.

**M4-L2 (Low).** `LicenseService.cs:128-129, 223` and `PlanAndCostServices.cs:157, 193` use `DateTime.SpecifyKind(x, Utc)`. System.Text.Json yields `Kind=Local` for offset-bearing input and `Unspecified` for none; `SpecifyKind` relabels instead of converting, so on a non-UTC host a license end date shifts by the host offset. Convert with `ToUniversalTime()` for `Local`; reject `Unspecified`, and add an upper bound on dates.

**M4-L3 (Low).** `Enumerable.Sum` over `int` is checked: `usable.Sum(l => l.Remaining)` (`LicenseMeteringService.cs:78, 88`, `PlanAndCostServices.cs:281-283`) throws `OverflowException` when a client's licenses add up to more than 2^31 credits (22 licenses of 100M). `Renew`/`AdjustCredits` do `TotalCredits += x` unchecked (`License.cs:182, 207`), surfacing as a DB CHECK failure (500). Use `long` sums and a `checked` guard in the domain.

**M4-L4 (Low).** Every consume bumps `RowVersion`, so `Suspend/Revoke/Adjust` (loaded then saved with the version) fail with 409 under heavy traffic to that license. The "kill switch" is probabilistic exactly when abuse is highest. Use a retry loop or a dedicated `UPDATE ... WHERE Status` for suspend/revoke.

**M4-L5 (Low).** `MeteringStore` and `LoginThrottle` run commands built directly from `DbConnection`, so the `TenantSessionContextCommandInterceptor` re-apply-if-scope-changed hook (`TenantSessionContextApplier.cs:44-53`) is skipped; they rely on the context applied at connection open. Fail-closed today (wrong tenant => 0 rows) but a scope switch in the same transaction would be silently ignored. Call `ApplyIfChanged` before these commands.

**M4-L6 (Low).** `ChargeCommand.IdempotencyKey` has no length or charset check; column is `varchar(100)`. An overlong key causes a SQL truncation error (500). Validate `[A-Za-z0-9_-]{1,100}`.

---

## M3: Client Management

### What was verified as sound
- Tenant comes from the credential only: `ClientPortalController` has no client id in any route; `ClientPortalService` uses `_currentUser.ClientId` and re-checks via `IClientQueries.GetUserAsync(clientId, userId)`, so another tenant's user id returns 404 (IDOR closed).
- Admin plane: every `ClientsController` action has a Platform-scope permission; `IsSystem` client returns 404 on every action.
- Over-posting: dedicated request records; `UpdateClientProfileRequest` has no `Notes`, status or code; `Apply` does not touch them.
- Privilege escalation on client roles: `ResolveRoleAsync` requires `RoleScope.Client` and that the caller holds every permission of the role being assigned (`ClientPortalService.cs:290-305`). Self-deactivation and last-admin guards exist. Role changes call `RevokeSessions` and invalidate the session cache.
- Status change revokes refresh tokens and invalidates the guard cache (`ClientService.cs:286-297`); `ClientAccessGuard` fails closed on unknown status; `SessionValidator` consults the guard on each request.
- Settings: catalogue-driven allow-list (unknown keys rejected), type and bounds validated, `ManagedBy` split (limits/quotas are Platform-only, `ClientSettingsService.cs:177-187`), changes audited with old/new. No raw SQL.
- Envelope encryption (`ClientKeyService.cs`): 256-bit random DEK per client from `RandomNumberGenerator`; AES-256-GCM with a 96-bit random nonce and 128-bit tag; AAD binds client id, purpose and key version (ciphertext cannot be moved across tenants or purposes), and the wrap AAD binds client id, version and KEK id; the KEK is required in production (`Program.cs:56-77` refuses ephemeral); the DEK is zeroed after wrapping. Key material is never returned by any endpoint. The raw `byte[]` plaintext length/format is validated before decrypt.

### Findings

**M3-M1 (Medium). Platform staff IPs and ids leak to client admins through audit logs.**
`AuditLogDto` includes `ActorId` and `IpAddress` (`ClientDtos.cs:43-44`, `ClientMapping.cs:22-23`) and `GET /api/v1/client/audit-logs` (`ClientPortalController.cs:60-63`) returns every audit row whose `ClientId` is the tenant, including rows written by platform staff (`client.suspended`, `license.adjusted`, `license.created`, `client.user_password_reset`, ... record the *target* client id: `ClientService.cs:157, 294`, `LicenseService.cs:151, 210, 233, 252`). A ClientAdmin therefore learns staff GUIDs and the source IPs (office, VPN, home) of support and finance staff.
Fix: for `ActorType != User/ApiKey of this tenant` return null `ActorId`/`IpAddress` (or a "Platform support" label); and consider hiding platform-only actions from the client view.

**M3-M2 (Medium). Internal `Notes` and `StatusReason` are returned to the client.**
`ClientPortalService.GetProfileAsync`/`UpdateProfileAsync` return `client.ToDto()` (`ClientPortalService.cs:109-110, 143`; `ClientMapping.cs:9-12`), which includes `Notes` (platform-internal; `UpdateClientProfileRequest` deliberately cannot set it) and `StatusReason` (suspension reason text). Any user with `client.profile.read` sees it.
Fix: a separate `ClientProfileDto` without `Notes`; show only a sanitised status message.

**M3-M3 (Medium). Delegated client user manager can modify a more-privileged user.**
`ClientPortalService.UpdateUserAsync` (`:198-263`) and `ResetUserPasswordAsync` (`:265-268`) check only that the *new* role is within the caller's permissions (`:290-305`), never that the *target's current* privileges are within the caller's. The platform fix for H-2 (`PlatformUserService.cs:317-322`) was not mirrored here.
Attack: platform creates a custom Client-scope role with `users.manage` but fewer permissions (roles are data; allowed by design). A holder of that role calls `PUT /api/v1/client/users/{adminId}` with role `ClientUser` / `IsActive=false` (allowed unless the target is the last active admin), or triggers password-reset mail and session revocation for the admin. Result: demote or lock out a ClientAdmin. No takeover (reset goes to the owner's mailbox), but integrity and availability impact within the tenant.
Fix: reject when the target's current permissions are not a subset of the caller's; also protect `IsOwner`. Also make the last-admin guard race-safe (M3-L4).

**M3-L1 (Low). Cross-tenant e-mail oracle on user creation.**
`ClientPortalService.cs:171-175`; `UserRepository.cs:22-23` is tenant-filtered, but the unique index on `NormalizedEmail` is global (`IdentityConfigurations.cs:19`). A ClientAdmin inviting `victim@other.com` gets 201 vs a conflict (unique violation mapped to 409), revealing whether that address has an account on the platform. Respond uniformly (queue an invitation e-mail either way) or accept the documented risk for B2B.

**M3-L2 (Low). Settings validation.**
`SettingCatalog.cs:160-176`: allowed-IP entries use `IPAddress.TryParse` (accepts short forms like `1` or `127.1`, scoped IPv6); origin check accepts `https://a.com?x=1`, `https://a.com#f` and `https://user:pw@a.com` (path is `/` and no trailing slash). When M6 consumes these as CORS/IP allow-lists, require exact normalised form (`uri.GetLeftPart(Authority) == item`, no user info/query/fragment) and canonical IP/CIDR round-trip. `face.retentionDays` (client-editable, up to 3650, `SettingCatalog.cs:50`) is not capped by a platform maximum as docs/04 §9 states. `ClientSettingsService.GetAsync/UpdateAsync` do not exclude the system client for platform callers.

**M3-L3 (Low). DEK cache and KEK rotation.**
`ClientKeyService.cs:142-175`: unwrapped DEKs are held in `IMemoryCache` for 10 minutes (plain `byte[]`, not zeroed on eviction). `DestroyKeysAsync` (`:128-140`, currently no caller) evicts only the local instance's cache, so after crypto-shredding other instances keep decrypting for up to 10 min. `Unwrap` always uses the *current* `MasterKeyId` in the AAD (`:194`) rather than the row's stored `MasterKeyId`, so a KEK rotation (bumping the id) makes every existing wrapped key undecryptable; no multi-KEK or re-wrap path exists. Fix before M5/M9: select KEK by `row.MasterKeyId`, implement re-wrap, shorten the cache TTL or add a cross-instance invalidation for shredding, and zero key bytes on eviction.

**M3-L4 (Low). Races.** `MaxUsers` count-then-insert (`ClientPortalService.cs:165-169`) and the last-admin check (`:218-222`) are read-then-write without a lock; concurrent requests can exceed the limit or leave zero admins. Use a serialisable check or a DB constraint.

---

## M2: Re-verification of the fixes

| Claimed fix | Result | Evidence |
|---|---|---|
| Lockout race (H-1) | **Effective** | `AuthService.cs:141-147` reserves an attempt *before* verifying, through one atomic `UPDATE ... OUTPUT` (`LoginThrottle.cs:25-35`) that either increments or locks; parallel guesses each consume one attempt, so a burst of N parallel guesses yields at most `MaxFailedAttempts` verifications. A request that raced past the early `IsLockedOut` check still sees the lock in the OUTPUT (`:56`). Locked and unknown users burn equivalent hash time and return the same generic error. Lockout blocks password sign-in only and cannot be extended while locked (no DoS amplification); password reset clears it. Change-password uses the same throttle (`AuthService.cs:335-342`). |
| Delegated-admin escalation (H-2) | **Effective** for platform users | `PlatformUserService.UpdateAsync` (`AccessControlService.cs:317-330`) requires the target's permissions to be a subset of the caller's, role assignment requires every permission to be held (`:377-387`), and a last-Super-Admin guard exists. `RoleService` refuses to edit a role containing permissions the caller lacks and refuses to grant what is not held (`:106-129, 192-198`); system roles are immutable (`EnsureEditable`). The same pattern is **missing for client users** (M3-M3). |
| Refresh rotation race | **Effective** | `RefreshTokenClaimer.TryClaimAsync` (`LoginThrottle.cs:80-83`) is a single `UPDATE ... WHERE RevokedAt IS NULL` returning 1 for exactly one caller; losers are refused and do not fork the family (`AuthService.cs:220-223`). Replay of a rotated token outside the 10 s grace window revokes the family (`:190-200`); expiry honours an absolute family cap (`RefreshToken.cs:48-49`). Residual: M2-L1. |
| Timing | **Effective, residual Low** | forgot/reset padded to a fixed minimum with cooldown and background mail (`AuthService.cs:256-278, 408-416`); login burns hash time for unknown/locked users. Residual M2-L3. |
| Least-privilege DB principal | **Effective with caveats** | `DatabaseInitializer.EnsureApplicationPrincipalAsync` (`:71-116`): `db_datareader` + `db_datawriter` + `EXECUTE`, `DENY ALTER ANY SECURITY POLICY`, `DENY UPDATE, DELETE` on every `IAppendOnly` table (includes `LicenseTransactions`, `AuditLogs`, `LoginHistory`); readiness fails if the login is sysadmin/db_owner/can alter security policies, enabled by `appsettings.Production.json` (`RequireLeastPrivilege: true`). See M2-L2/L4 for gaps. |

JWT validation re-checked: issuer, audience, lifetime, signed tokens required, `ValidAlgorithms = ES256` only, strict claim parsing, per-request session version check (`JwtBearerSetup.cs:34-69`). Permission handler enforces permission scope vs principal kind and denies principals that must change their password (`PermissionAuthorization.cs:92-110`). Good.

**M2-L1 (Low).** `AuthService.RefreshAsync` claims the old token with an autocommit statement (`:220`) and inserts the successor later in `SaveChanges` (`:225-226`). A crash or exception between the two leaves the user logged out (availability only). `LogoutAsync` (`:243-246`) revokes siblings from a stale load, so a concurrent refresh can create a successor that survives logout. Wrap claim + insert in one transaction; on logout revoke by `FamilyId` with a single UPDATE.

**M2-L2 (Low).** `TenantProtectionHealthCheck.cs:56-66` checks sysadmin/db_owner/ALTER ANY SECURITY POLICY only. A login in `db_ddladmin` (or with `ALTER` on the tables, `CONTROL`) could drop the append-only triggers; add `IS_MEMBER('db_ddladmin')` and `HAS_PERMS_BY_NAME(... 'CONTROL'/'ALTER ANY SCHEMA')` checks, and a check that the DENY exists on every append-only table. Note, as an accepted limit: RLS/triggers are defence-in-depth against application bugs, not against a fully compromised app process, because the app principal can itself call `sp_set_session_context` to set `IsPlatform=1`.

**M2-L3 (Low).** Known-user wrong-password path executes the reserve UPDATE plus hash verify, unknown-user path executes only a hash burn: a small constant timing difference remains (about one DB round-trip against a ~100 ms PBKDF2). Statistically measurable only with many samples; per-IP rate limit makes it impractical. Optionally run an equivalent dummy UPDATE for unknown users.

**M2-L4 (Low).** The unsafe-switch guard runs only when `IsProduction()` (`Program.cs:67-77`) and `RequireLeastPrivilege` is on only through `appsettings.Production.json`, so a Staging environment with real data would accept ephemeral keys and a sysadmin login. `src/Migrator/appsettings.json` commits `Jwt/Encryption: AllowEphemeralKey: true`; if the migrator ever provisions a client key it would be wrapped with a throw-away KEK and become unrecoverable. Make the guard environment-independent (`!IsDevelopment()`), and drop the Migrator defaults.

---

## Informational
- **M3-I1**: `Permissions.Settings.Security` clients can set `security.requireMfa` and IP allow-lists with no check that it will not lock the tenant out (e.g. empty-but-wrong list). Add a safeguard or break-glass note when M6 enforces them.
- **M4-I1**: `LicenseKey` is an identifier, not a secret, and is exposed to clients; make sure no future auth path accepts it as a credential. `DestroyKeysAsync` and `Revoke/Expire` write-off are not reachable from any API yet (no offboarding endpoint).
- Password reset URLs carry the token in the query string (`AuthOptions.cs:46`); acceptable with `Referrer-Policy: no-referrer`, but keep the reset page free of third-party resources.
- Architecture tests already confine raw SQL to `Infrastructure/Platform`; keep `ExecuteSqlInterpolated` usage as is.

## Required before sign-off
1. M4-M1 and M4-M2 designed into the M5 contract (and tested) before any endpoint calls `ChargeAsync`.
2. M4-M3 items 1, 3 and 4 (keyed hash or anchoring, unique `(LicenseId, PrevHash)`, nightly verification and alert) before M9 hardening, per docs/04 T9.
3. M4-M4, M3-M1, M3-M2 (client-facing DTO minimisation) before the client portal (M8) is exposed.
4. M3-M3 before any custom client roles are created in production.
5. After fixes, request re-verification; this review does not clear any item that was fixed afterwards.
