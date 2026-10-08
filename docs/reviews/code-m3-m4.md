# Code review: M3 Client Management and M4 Licensing and Metering

Reviewer: Code Review Agent. Scope: read-only review of the files named in the request, plus the M3/M4 tests.
`dotnet build -warnaserror` passes (0 warnings, 0 errors). Integration tests need SQL Server and were NOT run.
Conclusions about concurrency and EF behaviour come from reading the code.

## Verdicts
| Module | Verdict |
|---|---|
| M3 Client Management | **Approve-with-changes** |
| M4 Licensing and Metering | **Approve-with-changes** |

M4 has no data-integrity blocker. The core design is sound. Consumption is one conditional `UPDATE ... OUTPUT` that takes the row lock. The ledger tail is read only after that lock is held. The ledger is hash-chained, there are unique-index guards for idempotency and refunds, and the commit uses `CancellationToken.None`. The Majors below are reliability and performance defects that should be fixed before the face module starts calling the metering path.

## Ledger ordering check (the specific concern)
| Path | Order | Verdict |
|---|---|---|
| Charge (`LicenseMeteringService.cs:138-147`) | `TryConsumeAsync` (UPDATE takes the lock), then `AppendAsync` reads the tail, then `SaveChanges` | Correct |
| Revoke, Renew, Adjust, Refund, Expire (`LicenseService.cs:204,229,250,288`; `PlanAndCostServices.cs:366`) | `SaveChanges` (license UPDATE plus version check) first, then tail read, then a second `SaveChanges` | Correct |
| Create (`LicenseService.cs:147-155`) | Tail read before the license INSERT | Safe, because the license is new and has no other writers |

The invariant is documented on `LedgerWriter`, but nothing enforces it (see m-8).

## Findings

### Blocker
None.

### Major

**M-1. Admin credit operations race with metering and fail with a concurrency conflict.**
- Where: `LicenseService.cs:194,216,240,269,337` and `PlanAndCostServices.cs:349`.
- Revoke, Renew, Adjust, Refund, Suspend/Activate/Deactivate and the Expiry processor load the tracked `License` before opening the transaction.
- Every charge changes `ConsumedCredits` through raw SQL, which also bumps `RowVersion`.
- The later admin `SaveChanges` therefore fails the version check whenever a charge lands between load and save.
- This is the normal state for an active client, and the admin caller gets a 409 for no user-visible reason.
- `MeteringTests.Admin_changes_and_charges_interleave...` (line 380) retries `adjust` up to 10 times, which shows the problem is known.
- Fix:
  - Load the license inside the transaction delegate, as the first statement, for every operation that moves credits or status.
  - Or add a retry-once wrapper around `InTransactionAsync`.
  - Or take the lock explicitly with `UPDLOCK, ROWLOCK` in a store method and re-read.
- Reloading inside the transaction also makes `before = license.Remaining` accurate.
- Today `before` can be stale. It stays consistent only because the version check rejects the save.
- Only Update should use a client-supplied `RowVersion`. The other operations should not depend on it.

**M-2. Expiry processor: a failed license poisons the rest of the batch.**
- Where: `PlanAndCostServices.cs:343-386`.
- All licenses in the batch share one scoped `DbContext`.
- After `ConcurrencyConflictException`, the mutated `License` stays `Modified` in the change tracker.
- It may also leave a pending `LicenseTransaction` and audit row.
- The next iteration's `SaveChangesAsync` re-submits the stale entity and throws again, which is swallowed.
- The remaining licenses in the batch are then skipped until the next 5-minute tick.
- Any other exception type aborts the loop.
- Fix:
  - In the `catch`, call `ChangeTracker.Clear()` (via a `IUnitOfWork.DiscardChanges()`).
  - Better, process each license in its own DI scope.
  - Load inside the transaction, as in M-1.
  - Catch per item and log a warning with the license id, so one bad row does not stop the batch.
- The test (`MeteringTests.cs:337`) covers only the happy path. Add a conflict case.

**M-3. Hot metering path does 2 uncached rule queries per license, per call, and does it twice per request.**
- Where: `CostRuleResolver.cs:27-47`, `CostRuleRepository` (`LicensingRepositories.cs:157-161`), `LicenseMeteringService.cs:73-79,124-127`.
- For each usable license, `ResolveAsync` runs `ListClientRulesAsync` and `ListPlatformRulesAsync`.
- Both read the whole table, not just the one operation, and both are tracked.
- Preflight and Charge each repeat this, plus `GetUsable`.
- A typical one-license request makes about 3 + 7 round trips. Charge also runs these reads inside the open transaction.
- Tracked entities accumulate in the change tracker, and `AppliesAt` is filtered in memory.
- Fix, in order of value:
  - Resolve cost once per `(plan, operation)`, not per license. Pass the `MeterTicket` (cost and policy) from Preflight into Charge, or cache it per request.
  - Push the filter to SQL: `WHERE Operation = @op AND EffectiveFrom <= @at AND (EffectiveTo IS NULL OR @at < EffectiveTo)`.
  - Add `AsNoTracking()` for read-only resolution. Keep tracked reads for `CostRuleService`.
  - Cache platform rules in a short-TTL `IMemoryCache` entry, invalidated in `SetPlatformRuleAsync`. They change rarely.
  - Compute the cost before `ExecuteInTransactionAsync` so the transaction contains only consume, tail read and insert.
- Also, `GetUsableAsync` (`MeteringStore.cs:23-27`) loads full `License` rows. Project the needed columns: Id, PlanId, ExpiresAt, Remaining.

**M-4. Portal `ResetUserPassword` over-fetches every active refresh token in the client.**
- Where: `ClientService.cs:226-230` and `ClientQueries.cs:121-122`.
- It loads all active tokens for the whole tenant, tracked, then filters by `userId` in memory.
- A `GetActiveForUserAsync(userId)` already exists on `IRefreshTokenRepository` and is used in `ClientPortalService.cs:245`.
- Fix: use the per-user query here.
- `ChangeStatusAsync` (`ClientService.cs:288`) legitimately revokes all of a client's tokens, but it is still unbounded and tracked.
- Prefer `ExecuteUpdateAsync` setting `RevokedAt` and the reason, or at least page the revocation.

### Minor

**m-1. Charge uses the request cancellation token for the whole billing transaction.**
- Where: `LicenseMeteringService.cs:108-147`.
- Only the commit is protected by `CancellationToken.None` (`AppDbContext.cs:101`).
- The caller has already spent CPU on image processing. A client disconnect during `TryConsume` or `SaveChanges` rolls back, so the work is delivered uncharged.
- Fix: the face module's call to `ChargeAsync` should pass `CancellationToken.None`, or the service should do so internally after the pre-flight succeeds.
- Document the choice in the `ILicenseMeteringService` summary.

**m-2. Idempotent replay does not check that the key's original request matches.**
- Where: `LicenseMeteringService.cs:101-103,161-162`.
- The same key with a different `Operation` or `RecognitionRequestId` silently returns the old charge.
- Compare, and return 409 on mismatch (or document that the key is the identity).
- Replay of an operation that was free or not billable returns `Charged 0, Replayed false` and writes no ledger row. That is acceptable, but note it in the docs.

**m-3. `CostRule` and `ClientCostRule` duplicate the same logic.**
- Where: `CostRule.cs:28-36` and `CostRule.cs:65-73` (validation and the magic `1000`), `AppliesAt`, and `CloseAt`.
- Two near-identical `Map` overloads and set-rule methods are in `PlanAndCostServices.cs:144-214`.
- The upper bound `1000` also appears in `LicensingConfigurations.cs:49,59`.
- Fix:
  - Introduce `CostRule.MaxCredits` as a constant.
  - Add an `ICostRuleLike` interface or a shared `CostTerms` value object with `AppliesAt`, `CloseAt` and validation.
  - Extract `SetRuleAsync<T>(...)` for the shared parse, close-open-rules and add flow.

**m-4. Cost rule versioning is not transactional and can leave overlapping rules.**
- Where: `PlanAndCostServices.cs:168-176,204-212`.
- It loads all rules and closes those with `EffectiveFrom < from`. A rule with equal `EffectiveFrom` stays open.
- Two concurrent PUTs can both pass.
- Fix:
  - Use `<=`, or reject a duplicate `EffectiveFrom`.
  - Add a filtered unique index on `(PlanId, Operation) WHERE EffectiveTo IS NULL`.
  - Filter by plan and operation in SQL instead of loading all rules.

**m-5. `NewKeyAsync` and `CreateAsync` check-then-insert race.**
- Where: `LicenseService.cs:379-391,147`.
- The license-key check is TOCTOU. The unique index will throw `UniqueConstraintViolationException`, which is not caught here.
- Drop the pre-check and retry on the unique violation.
- Also, `CreateAsync` calls `_licenses.Add` outside the transaction delegate.

**m-6. `TryVersion` / base64 rowVersion parsing is copy-pasted.**
- Where: `LicenseService.cs:393-405`, `ClientService.cs:301-313`, and inline in `ClientPortalService.cs:126-134`.
- Move it to `Application/Common` as `RowVersion.TryParse`.
- `Clean(string?)` is duplicated in `ClientMapping.cs:46` and `ClientPortalService.cs:307`.
- `Client.Apply(...)` has 13 positional parameters. Pass the request or a small record instead.

**m-7. Repeated "Only client users ..." guard in `ClientPortalService`.**
- Where: ten places, e.g. `ClientPortalService.cs:104-108,115-119,160-164`.
- Add a `TryGetClientId(out Guid, out Error)` helper, or a `RequireClient()` that returns `Result<Guid>`.
- The tail methods (`ResetUserPassword`, `GetActivity`, `GetLogins`, settings) re-implement the same ternary five times.

**m-8. The ledger invariant is a convention, not enforced.**
- Where: `LedgerWriter.AppendAsync` (`LicenseSupport.cs:41-60`).
- A future caller that appends before taking the row lock would silently break the chain.
- Make it fail fast:
  - In `AppendAsync`, assert `Database.CurrentTransaction != null`.
  - Require a proof parameter such as a `LockedLicense` token returned by `TryConsume` or `SaveChanges`.
  - Or take `UPDLOCK` in `GetTailHashAsync`, so the tail read is safe on its own.
- The unique index on `(LicenseId, PrevHash)` would also turn a race into a hard failure. It is not present.

**m-9. Expiry sweeper details.**
- Where: `LicenseExpirySweeper.cs:26-47`.
- The first tick runs immediately at startup, before migrations or the seed may have run. Delay the first iteration.
- The batch size `100` and the interval are hard-coded (`LicenseExpirySweeper.cs:12`). Move them to `LicensingOptions`.
- The processed count is logged only when it is greater than 0. Also log "batch full, more pending" when the count equals the batch size.
- No distributed lock. Multiple instances would double-run, which is harmless today because of version checks, but state it.

**m-10. `ClientKeyService` cache handling.**
- Where: `ClientKeyService.cs:144-172` and `128-140`.
- Plaintext data keys sit in `IMemoryCache` for 10 minutes and are never zeroed on eviction. Add `PostEvictionCallbacks` that call `CryptographicOperations.ZeroMemory`.
- `DestroyKeysAsync` invalidates only the local cache. On other nodes a destroyed key stays usable for up to 10 minutes. Document this, as `ClientAccessGuard` does for its TTL.
- `GetOrCreateAsync` has no per-key single-flight, so cold keys cause a thundering herd.
- `DestroyKeysAsync` calls `_db.SaveChangesAsync` directly and writes no audit record. Route it through `IUnitOfWork` and `IAuditService`.
- `ProvisionAsync` returns a completed Task and does no I/O. Rename it or make it synchronous.
- No key-rotation path: the version is hard-coded to `1` (line `~85`).

**m-11. `JsonDocument` is never disposed.**
- Where: `ClientSettingsService.cs:172,191` (`JsonDocument.Parse(json).RootElement.Clone()`).
- This leaks pooled buffers on every call.
- Fix: `using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone();`.
- `GetEffectiveAsync` also reads every override and parses every setting on each call. Cache per client with short-TTL invalidation on `UpdateAsync` before the face or rate-limit modules call it on a hot path.
- `ClientSettingsSnapshot.Int/Bool/...` throw `KeyNotFoundException` for an unknown key. Use a typed accessor with a catalogue default.

**m-12. `ClientPortalService.ResolveRoleAsync`.**
- Where: `ClientPortalService.cs:290-305`.
- It passes `CancellationToken.None` to `GetPermissionsAsync`, inconsistent with the rest of the method.
- It loads the whole permission catalogue on each call and indexes it with `catalogue[p.PermissionId]`, which can throw `KeyNotFoundException`.
- Fix: use `role.Permissions` with `Include(p => p.Permission)` or a targeted query, and `TryGetValue`.

**m-13. Race conditions on soft limits.**
- Where: `ClientPortalService.cs:165-169` (MaxUsers) and `:219-222` (last admin).
- Both are check-then-act.
- Accept this and document it, or serialise the check inside a transaction with a lock on the client row.

**m-14. Magic strings.**
- Where:
  - `ClientService.cs:281` compares `ex.Code == "CLIENT_SUSPEND_REASON_REQUIRED"`.
  - Domain codes such as `"LICENSE_CREDITS_INVALID"` are repeated in `License.cs`.
  - `"USER_LIMIT_REACHED"`, `"LAST_ADMIN"`, and the audit action strings `"license.created"` etc. are inline.
  - `ClientPortalController.cs:18-20` hard-codes the setting group names `Recognition`, `Notifications` and `Security`/`Integration`.
  - `"PlatformDefault"` / `"Plan"` / `"Client"` in `Map` (`PlanAndCostServices.cs:235`) duplicates `CostRuleScope` names.
- Move these to constants (`DomainErrorCodes`, `AuditActions` as already done for users, `SettingGroups`) and use `CostRuleScope.ToString()`.

**m-15. Domain model details.**
- `License.Revoke` stores the revoke reason in `SuspendedReason` (`License.cs:145`). That is a reuse that will confuse reports. Add `RevokedReason` or a generic `StatusReason`.
- `License.IsUsable` duplicates the SQL predicate in `MeteringStore.TryConsumeAsync`. A test should assert they agree, because the raw SQL hard-codes `'Active'` instead of deriving it from the enum conversion.
- `ClientId` has a public setter on `License`, `ClientCostRule` and `LicenseTransaction`, which breaks encapsulation, though it is dictated by `ITenantOwned`.

**m-16. `ClientService` and `ClientPortalService` have too many dependencies.**
- Where: 15 constructor parameters in `ClientService.cs:62-77` and 17 in `ClientPortalService.cs:62-79`.
- This points to mixed responsibilities: client lifecycle, user administration, activity and password reset.
- `ClientPortalService` also depends on the platform `IClientService` (`:361`) just to forward three calls. The portal then inherits the platform "IsSystem" rule.
- Suggested split: `IClientAdminService` (create, update, status), `IClientUserAdminService` (users, reset), `IClientActivityService`.
- Share a client-scoped core between the platform and portal facades, as `ClientQueries` already does.

**m-17. Smaller items.**
- `ClientService.CreateAsync` (`:160`) sends the invitation e-mail after the commit. If `SendAsync` throws, the caller sees a 500 for a client that was created. Catch and log it, and return success with a "resend" hint, or use an outbox.
- Concurrent duplicate `Code` / email relies on the unique index. Confirm `UniqueConstraintViolationException` maps to 409 in the middleware, and add a test.
- `LicenseRepository.ListAsync` uses `Contains` (`LicensingRepositories.cs:51-55`), which is a `%term%` scan on Name and LicenseKey. Fine at current scale. Use `EF.Functions.Like` with escaping, as `ClientQueries.EscapeLike` does, to be consistent and to escape `%` / `_`.
- `Rows(...)` does 2 extra queries per list or get. This is not N+1, but `GetRowAsync` does 3 round trips per read. Project in one query with joins.
- `LedgerRepository.GetAllAsync` (`LicensingRepositories.cs:~123`) and `VerifyLedgerAsync` load the whole ledger. Stream or page it.
- Three controllers are in one file, `LicensingControllers.cs`. Other modules use one controller per file. Split for consistency.
- `ClientQueries.ProjectUsers` picks the alphabetically first role (`ClientQueries.cs:128`) for users with several roles. Clarify the intent.
- `ClientSettingsService.GetAsync` (`:74`) uses `GetByIdAsync` without an `IsSystem` check, unlike the other platform endpoints.

### Consider
- Add a `LicensingOptions` class for the sweeper batch size and interval, the rule credit cap, and the key-generation attempt count (`LicenseService.cs:381`, `5`).
- `LicenseKeyGenerator.Generate` allocates many small LINQ objects. This is fine because it is not on the hot path.
- `MeteringStore.TryConsumeAsync` builds a `DbCommand` by hand. Consider `Database.SqlQuery<T>`. That needs an `OUTPUT INTO` or a CTE for the result, so the current approach is acceptable. The architecture tests confine it to `Platform/`, which is good.
- `MeterTicket.AvailableCredits` sums over `usable`, but cost may differ per license. Document that the ticket describes the first affordable license only.
- Structured logging in the metering path: there is none. Add `LogDebug` for `ChargeResult` (client id, operation, cost, license id, correlation id) and `LogWarning` for a lost race or an idempotency conflict. The ledger already stores the correlation id.

## Duplication report
| Duplicate | Locations | Suggested home |
|---|---|---|
| Base64 rowVersion parsing | `LicenseService.cs:393`, `ClientService.cs:301`, `ClientPortalService.cs:126` | `Application/Common/RowVersion.TryParse` |
| `Clean(string?)` | `ClientMapping.cs:46`, `ClientPortalService.cs:307`, and inline in `LicenseService.cs:145,177` | `StringExtensions.NullIfBlank()` |
| Cost-rule entity logic and mapping | `CostRule.cs:28-36` vs `65-73`, `PlanAndCostServices.cs:144-214` | shared base or value object |
| Client-user guard and ternary forwarding | `ClientPortalService.cs` (about 10 places) | helper returning `Result<Guid>` |
| Status-filter parse (`Enum.TryParse` plus paging normalise) | `LicenseService.cs:80-91`, `ClientService.cs:100-111`, `TryParse` in `PlanAndCostServices.cs:216` | generic `EnumFilter.TryParse<T>` |
| Page-request normalise pattern | about 6 service methods | `PageRequest.From(query)` helper |
| "Transition inside transaction" pattern | `LicenseService.cs` `InTransactionAsync` vs `ClientService.ChangeStatusAsync` | acceptable; different aggregates |
| `Client.Apply` for create and update | `ClientService.cs:143,179`, `ClientPortalService.cs:128-129` | pass the request object |

## Tests
- Strong: concurrency (80 racing charges, idempotent replay, ledger tamper detection, DB check constraint), FEFO, and the isolation tests.
- No unit tests for `LicenseMeteringService` or `CostRuleResolver` with fakes. Everything depends on SQL Server, so regressions cannot be found cheaply. Add pure unit tests for resolver precedence (client, then plan, then default, then fallback, with effective-date edges) and the charge branches (free, not billable, insufficient, lost race).
- `MeteringTests.cs:~155`: `results.Count(r => r.Value!.Replayed).ShouldBeGreaterThanOrEqualTo(0)` is always true. Assert at least one winner (`Replayed == false` count is exactly 1).
- `MeteringTests.cs:380`: the interleave test retries and never asserts that any adjust succeeded or that the failures were 409s. Assert the final balance equals the seed plus successful adjusts minus charges. After M-1 is fixed, remove the retry loop.
- Tests use `DateTime.UtcNow` rather than a fake `TimeProvider`. The margins are minutes, so flakiness is unlikely, but boundary tests (the exact `ExpiresAt` instant, the `StartsAt` boundary) cannot be written deterministically.
- The sweeper test calls `ProcessAsync(100, default)` and asserts `>= 1`. It will break if other test classes share the database. Assert on the specific license ids.
- Missing cases: refund racing a charge (M-1), sweeper conflict recovery (M-2), `ChargeAsync` cancelled mid-flight, and a unique-violation mapping test for duplicate client code.

## Refactor suggestions (ordered)
1. Reload-in-transaction (or retry) helper for all license mutations (M-1, M-2, m-8).
2. Metering hot path: single cost resolution per request, SQL-side filtering, `AsNoTracking`, platform-rule cache, projected `GetUsable` (M-3).
3. Per-user token query and bulk revoke (M-4).
4. Shared helpers: `RowVersion.TryParse`, `NullIfBlank`, `RequireClient`, `EnumFilter` (duplication report).
5. Split `ClientService` / `ClientPortalService` by responsibility (m-16).
6. Introduce `LicensingOptions` and constants for codes, groups and actions (m-9, m-14).

## What was done well
- Thin controllers returning `ToActionResult(Result<T>)`, and consistent permission attributes.
- Atomic conditional UPDATE for consumption, with a DB check constraint as a backstop.
- Hash-chained append-only ledger with a verifier, filtered unique indexes for idempotency and refunds, and a `NoMatch`-aware charge policy.
- The time-based "effective status", so late sweeps cannot allow a charge on an expired license.
- Injected `TimeProvider` is used throughout the services.
- Tenant-scoped idempotency, and no raw SQL outside `Infrastructure/Platform`.
- A well-bounded `ClientAccessGuard` TTL cache with explicit invalidation, and envelope encryption with authenticated data bound to the client and purpose.
