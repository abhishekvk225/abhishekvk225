# Code review: M6 API Management (API keys, usage limiting, request log, webhooks)

Branch `claude/face-recognition-saas-nv5djm`. Reviewed against docs/01, docs/02, docs/03 and the analyzer rules.

**Verdict: APPROVE-WITH-CHANGES.** There are no Blockers. The dependency rule holds, the controllers are thin, the services return `Result<T>`, the outbox row is staged in the same unit of work as the charge, and the SSRF design (re-checking addresses in `ConnectCallback`) is sound. There are 8 Major findings. The first three (M1 to M3) should be fixed before merge. The rest can be fixed in this milestone or ticketed.

Verification:
- `dotnet build -warnaserror` passed with 0 warnings and 0 errors.
- `Application.UnitTests` (73) and `Infrastructure.UnitTests` (55) passed.
- I did not run the SQL Server integration tests (`ApiKeyTests`, `WebhookTests`), as instructed. Findings about them come from reading the code.
- I did not check the SQL Server runtime behaviour of the `WebhookStore` claim query.

## Major

| # | Where | Problem | Suggested fix |
|---|---|---|---|
| M1 | `ApiKeyService.cs:162-201` (`RegenerateAsync`) | Regenerate copies `old.ScopeList` into the replacement and returns the raw key without calling `CheckScopesAsync`. Create and Update enforce "a key can never out-rank its creator". A user with `apikeys.manage` but fewer face scopes can regenerate a broader key and receive its secret. It also skips the `MaxApiKeys` check, so a grace-period regenerate leaves two active keys while the limit is 5. | Run the held-scopes check on `old.ScopeList` before creating the replacement. Decide explicitly whether a grace-period pair counts against the limit. Add integration tests for both. |
| M2 | `ApiKeyAuthenticator.cs:123-137` (`TouchAsync`), called from line 83 | The last-used write is awaited inline with the request's token, so a transient DB error or cancellation fails a request that is otherwise authenticated (a 500, or an `OperationCanceledException`). The throttle entry is set before the write (line 131), so a failed write suppresses retries for a minute. The comment says "informational", but the code does not treat it that way. | Wrap it in try/catch and log at Debug or Warning. Set the throttle entry only after success. Preferably hand the write to a background updater (a channel, like `ApiRequestLogWriter`) so it adds no latency to the hot path. |
| M3 | `WebhookDispatcher.cs:147-165` and `DeliverAsync` generally | The `catch` covers only `HttpRequestException`, `TaskCanceledException` and `InvalidOperationException`. A decryption failure (`CryptographicException`, for example after key rotation), a `UriFormatException` or a `FormatException` escapes to the outer Parallel wrapper, which logs at Error. That path never calls `MarkFailed`, so `Attempts` never increases. The row stays `Pending`, its lease expires after 2 minutes, and it is claimed again, forever, with an Error log every 2 minutes (a poison message). Separately, a `TaskCanceledException` caused by shutdown (`stoppingToken`) is recorded as "did not answer in time". That bumps the delivery's attempt count and the endpoint's `FailureCount`. | Treat any non-cancellation exception during send as a failed attempt (`catch (Exception ex) when (!cancellationToken.IsCancellationRequested)`). Persist an attempt on the unexpected-error path in the outer wrapper too. Filter shutdown cancellation out of the failure branch. Consider incrementing `Attempts` at claim time so a crash mid-send still counts toward `MaxAttempts`. |
| M4 | `ApiKey.cs:133` (rate limit up to 100,000), `ApiUsageLimiter.cs:62` (`credentialLimitPerMinute ?? perMinute`), `SettingCatalog.cs:53` | `api.rateLimitPerMinute` is a Platform-managed setting. A client admin can create a key with `RateLimitPerMinute = 100000` and bypass it. The limiter comment says a key "may lower or raise" its limit. Raising looks like an unintended policy hole. | Use `Math.Min(credentialLimitPerMinute ?? client, client)`, or add a platform-managed ceiling setting. Validate the key's value against the client's effective limit on create and update. If raising is intended, document it in docs/03. |
| M5 | `WebhookDispatcher.cs:97-119`, `ExecuteAsync` lines 82-93 | Each cycle claims at most 50 rows, then waits for the next 2 s tick, so throughput is capped at about 25 deliveries/s per node. Every recognition produces a delivery per subscribed endpoint, so a busy client builds an unbounded backlog. The lease (`Lease = 2 min`, claimed up front) is also coupled to batch time. With `TimeoutSeconds` clamped up to 30 s, `ceil(50/8) x ~30 s` is about 210 s, which exceeds the lease. Late items are then re-claimed while the first attempt is in flight and are delivered twice. Duplicates are tolerated by `X-Event-Id`, but the cause is avoidable. | Loop `ClaimDueAsync` until it returns fewer than the batch size before waiting. Move batch size, parallelism, interval and lease into `WebhookOptions`. Validate `lease > batch/parallelism x timeout`, or claim in smaller slices. |
| M6 | `WebhookDispatcher.cs:172-181`, `184-198`, `Webhooks.cs:38` | `DisableAfterConsecutiveFailures = 20` counts failed attempts, not failed events. Twenty different events failing once each during a short receiver outage (a 1-minute blip) disable the endpoint. The test `An_endpoint_that_keeps_failing_is_disabled_automatically` encodes this: 20 distinct deliveries fail in a single dispatch. A global client-visible outage then needs manual re-enable. | Count only "attempts that exhausted retries" (abandonments), or require failures over a minimum time window (for example, failing for more than 1 hour and at least 20 failures). Also reset the counter on a successful delivery of any event, which already happens. |
| M7 | Whole of M6 (no purge anywhere; only `ApiRequestLogWriter.PurgeAsync` exists for logs) | `api.WebhookDeliveries` is never purged. Delivered and Abandoned rows with `nvarchar(max)` payloads grow for ever, and one is written per recognition per endpoint. | Add retention (for example `WebhookOptions.RetentionDays`) as a batched `DELETE TOP (n)` for non-Pending rows older than the cutoff. Run it in the same hosted service pattern as the log purge. |
| M8 | `Webhooks.cs:20-31`, `WebhookService.cs:100-108`; only publisher call is `FaceRecognitionService.cs:585` | `license.low_balance`, `license.expiring`, `license.expired`, `license.exhausted` and `apikey.expiring` are advertised by `Events()`, accepted by validation and listed in docs/03 line 166, but nothing ever publishes them. Clients can subscribe to events that never fire. | Wire the publishers (the metering or licence jobs and a key-expiry sweep), or remove them from `Subscribable` and `Events()` until implemented. Add a test per advertised event. |

## Minor

| # | Where | Problem | Suggested fix |
|---|---|---|---|
| m1 | `ApiKeyAuthenticator.cs:99-118` | Unknown-prefix results are cached under attacker-chosen keys for 15 s with no size limit on `IMemoryCache`. With the IP rate limiter in front it is bounded, but it is still memory churn. There is also no single-flight, so concurrent first requests all hit the DB (two queries per miss). | Cap entries (`SetSize` and a cache size limit, or a dedicated small cache). Combine the two queries, or cache the client rules separately. Comment why `CancellationToken.None` is used. |
| m2 | `ApiKeyAuthenticator.cs:72-73, 84` | Each request re-splits `AllowedIps` and `Scopes` and re-parses the IPs in `IpRules.Allows`. These are hot-path allocations. | Store pre-parsed arrays (and parsed `IPNetwork` values) in `Known`. |
| m3 | `ApiKeyAuthenticator.cs:62-81` | Authentication failures (revoked, expired, IP denied, suspended) are not logged. Security monitoring has nothing to alert on. | Log at Information or Warning with key id, prefix and client id (never the raw key) and the reason code. |
| m4 | `ApiUsageLimiter.cs:58` | The comment says the daily quota is checked first, but the code takes the minute window first and rolls it back (lines 82-95). The lock nesting minute then day then minute is deadlock-free but fragile. A `Sweep` that removes an entry while a thread still holds it briefly lets that thread count on a detached object. | Fix the comment or reorder (day then minute, with the rollback on the day counter). Note in the class docs that counters are per node and reset on restart, so quotas are not authoritative across nodes. |
| m5 | `ApiUsageLimiter.cs:27` | `_limits` grows by one entry per client and is never swept. This is small but unbounded. | Sweep it together with `_minute` and `_day`. |
| m6 | `ApiUsageMiddleware.cs:52-56` | `catch { Response.StatusCode = 500; throw; }` throws `InvalidOperationException` if the response has already started, which masks the original exception. | Guard with `if (!context.Response.HasStarted)`. |
| m7 | `ApiUsageMiddleware.cs:78-79` | The IP is logged with `RemoteIpAddress.ToString()` without the IPv4-mapped normalisation done in `ApiKeyAuthentication.cs:37`. The UA is held in the queue untruncated (up to 20,000 entries). | Extract one `ClientIp` helper (also used by `ServiceCollectionExtensions.cs:182`). Truncate the UA when enqueueing, to the same bound as the domain. |
| m8 | `ApiRequestLogWriter.cs:43-48, 60` | The channel is `SingleReader = true`, but the public `FlushAsync` (used by tests) can run while the background loop reads, which is unsupported. The purge is a single unbatched `ExecuteDeleteAsync`, so a first purge after a long gap could escalate locks. | Remove `SingleReader` or serialise flushes with a `SemaphoreSlim`. Batch the purge with `DELETE TOP (n)` in a loop. |
| m9 | `WebhookUrlGuard.cs:76-83` | The 5 s timeout is a literal and ignores `WebhookOptions.TimeoutSeconds`. Caller cancellation is turned into "host could not be resolved". `Dns.GetHostAddressesAsync` throws `ArgumentException` for names over 255 characters, which surfaces as a 500. | Use an option. Rethrow cancellation of the caller's token. Catch `ArgumentException`. |
| m10 | `WebhookUrlGuard.cs:101-125` | `IsPublic` is a good list but misses 192.0.0.0/24 beyond .0 and .2, 192.88.99.0/24 (6to4 relay), 2002::/16 (6to4), 2001::/32 (Teredo) and ::/96 (IPv4-compatible). It also never tests `ConnectCallback` against a non-public address, because every integration test runs with `AllowUnsafeTargets = true`. | Add the missing ranges and unit-test them. Extract the `ConnectCallback` filter into a testable static method. Also try the next allowed address if `allowed[0]` fails (`WebhookDispatcher.cs:62`). |
| m11 | `WebhookStore.cs`, `WebhookDispatcher.cs:103` | `WebhookStore` is a concrete class with no interface, so the dispatcher cannot be unit-tested with a fake. | Extract `IWebhookClaimStore` in Application or Infrastructure. |
| m12 | `ApiKeyService.cs:99-102`, `WebhookService.cs:149-152` | The API key and webhook CRUD paths have no logging, and the only audit entries come from `_audit`. `SendTestAsync` and `RetryAsync` are not audited. The create-key limit check (count then insert) is not atomic, so two concurrent creates can exceed `MaxApiKeys` by one. | Audit `webhook.test_sent` and `webhook.delivery_retried`. Accept or document the small race on the limit. |
| m13 | `Infrastructure/Persistence/Repositories/ApiRepositories.cs:24` and `Application/Persistence/Repositories.cs:321` | `PrefixExistsAsync` is dead code. A prefix collision (36^8) would surface as an unhandled unique-constraint exception on create. | Remove it, or retry key generation once on `UniqueConstraintViolationException`. |
| m14 | `ApiLogRepository.ListAsync` (`ApiRepositories.cs:122-152`) | `Count` plus page over `ApiRequestLogs` with an optional date range can scan the whole retention window (90 days). `statusClass` is a free string whose unknown values are silently ignored. | Default `From` to, for example, the last 24 hours or 7 days with a maximum range. Parse `statusClass` into an enum and fail validation on unknown values. |
| m15 | `ApiConfigurations.cs:17`, `ApiValidators.cs:14` | `AllowedIps` is `varchar(2000)`, while the validator allows 50 entries of up to about 49 characters each (CIDR IPv6), which is roughly 2,500 characters. This would give a SQL truncation error (500). | Lower the entry cap or raise the column. Validate the joined length in the domain `Apply`. |
| m16 | `ApiConfigurations.cs:74` | `PayloadJson` uses `HasColumnType("nvarchar(max)")`, so the convention's 256 `MaxLength` metadata stays in the model (`maxLength: 256` in the migration at line 196). The real column is `nvarchar(max)`, matching `AuditLogConfiguration` and `TenancyConfigurations`, so the behaviour is correct. | No action required; consistent with existing practice. I checked the other string columns against the 256 convention and found no unintended caps. |
| m17 | `ApiKeyAuthentication.cs:61-68` | The challenge response omits a `WWW-Authenticate` header (RFC 9110). | Add `WWW-Authenticate: ApiKey` (or the scheme name). |

## Consider

- `ApiKey.Create` and `Update` accept `request.Scopes` of any length. The validator caps each scope at 60 characters, but the domain does not check. Move the scope-length check into the domain next to the `MaxScopes` check.
- The magic numbers `20` (scopes), `100_000`, `50` and `100` are repeated in `ApiValidators.cs`, `ApiKey.cs` and the `Webhooks.cs` validators. Use `ApiKey.MaxScopes` and shared constants.
- `WebhookDelivery.Abandon(reason, now)` discards `now` with `_ = now;`. Remove the parameter.
- `WebhookDelivery` has no concurrency token. A manual `RetryAsync` racing the dispatcher's save is a last-writer-wins case.
- Add `FailureCount` and `Attempts` to a metrics counter (OpenTelemetry), and the dropped-log-entries counter in `ApiRequestLogWriter.cs:53`.
- `HttpClient` in `WebhookDispatcher` is never disposed (`BackgroundService.Dispose` is not overridden).
- `SendTestAsync` has no per-endpoint throttle.

## Duplication report

| Duplicated item | Locations | Suggestion |
|---|---|---|
| `TryVersion(string, out byte[])` (identical bodies) | `ApiKeyService.cs:221`, `WebhookService.cs:281`, `LicenseService.cs:394`, `ClientService.cs:300` | Move to a shared `RowVersion.TryParse` in `Application.Common`. |
| `Utc(DateTime?)` helper | `ApiKeyService.cs:219` (`ApiKeyService`) and `ApiKeyService.cs:265` (`ApiLogService`) | One shared extension. |
| Create/Update API key validators repeat the five identical rules | `ApiValidators.cs:10-15` and `23-28` | Share a base class or a rule-set extension. The same applies to `CreateWebhookRequestValidator` and `UpdateWebhookRequestValidator` (`WebhookService.cs:302-320`). |
| Scope, IP and size limits as literals | `ApiValidators.cs`, `ApiKey.cs` | Use the domain constants. |
| Remote-IP normalisation (IPv4-mapped to IPv4) | `ApiKeyAuthentication.cs:37`, `ServiceCollectionExtensions.cs:182`, `ApiUsageMiddleware.cs:78` | One helper on `HttpContext`. |
| "Truncate to N characters" | `ApiRequestLog.cs:59`, `Webhooks.cs:90,183,201` | A shared string extension. |
| "Not found, revoke, save, invalidate cache" pattern | `ApiKeyService` Update, Revoke and Regenerate | Acceptable; a small private helper would shorten it. |

## Test review

Existing tests are meaningful (a full flow with a key, hash storage, revocation, the redirect-not-followed check and the SSRF address table). Issues and gaps:

- **Flaky: `A_key_is_throttled_per_minute_and_other_keys_are_unaffected`** (`ApiKeyTests.cs:239-255`). The limiter uses a fixed minute window on the wall clock, so if the minute rolls over between the 3 calls and the 4th, the 429 assertion fails. Inject a `FakeTimeProvider` (the limiter already takes `TimeProvider`) or move the test to a limiter unit test.
- **Timing-based: `A_slow_receiver_times_out_instead_of_holding_the_dispatcher`** (`WebhookTests.cs:245-257`). It asserts under 3.5 s against a 4 s delay and a 1 s timeout, which is sensitive to CI load. Assert on `LastError` alone, or widen the margin.
- **Weak: `Regenerating_replaces_the_key...`** (`ApiKeyTests.cs:219-236`). It never shows the old key stops working after the grace period, only that it still works inside it. Assert on `ExpiresAt` and check expiry with a fake clock.
- **Weak: `Updates_need_the_current_version...`** (`ApiKeyTests.cs:337-338`). `ShouldBeOneOf(Conflict, NotFound)` accepts two different behaviours. Assert the one the code produces (Conflict, `APIKEY_REVOKED`).
- **IP tests are negative-only** (`ApiKeyTests.cs:189-200`). There is no positive case (an allowed IP succeeds), no client-level `AllowedIps`, and no mixed v4/v6 case.
- `WebhookUrlGuardTests.cs` lives in the `Faces` folder and namespace but tests webhooks. Move it, and add `ValidateAsync` cases (userinfo, fragment, over-long host).

Missing cases (priority order):
1. A limiter unit test with a fake clock covering minute-window rollover, **daily-quota day rollover** across UTC midnight, exact concurrency (N parallel callers get exactly `limit` successes), `Sweep` eviction, and the minute-budget refund when the daily quota rejects.
2. **Two dispatchers claiming concurrently**: seed N pending rows, run two `ClaimDueAsync` calls in parallel, and assert disjoint id sets and that no row is sent twice. Also lease expiry (a row re-claimed after `Lease`), and a multi-attempt claim where a dispatcher dies mid-send and `Attempts` is not incremented (the poison case in M3).
3. A delivery whose endpoint secret cannot be decrypted, or whose URL is malformed (the M3 poison case), to prove it is abandoned and not retried for ever.
4. **Queue overflow** in `ApiRequestLogWriter`: a capacity of 100 with more `Enqueue` calls, asserting drops are counted and the API call is not blocked. Also purge and retention.
5. **`X-Api-Key` plus a bearer token both present.** The policy scheme picks the key (`ServiceCollectionExtensions.cs:59`), so an invalid key with a valid bearer must be refused (401) and not fall back. Also two `X-Api-Key` headers (400-class answer) and surrounding whitespace.
6. Regenerate/update scope escalation (M1), the rate-limit ceiling (M4), cross-tenant isolation of `api-logs`, and `ApiLogs` paging and filters.
7. `ConnectCallback` filtering against non-public addresses with `AllowUnsafeTargets = false` (extract and unit-test).
8. A grace-period regenerate against the `MaxApiKeys` limit.
9. A domain test for `WebhookDelivery.MarkFailed` backoff (deterministic bounds with the ±10 % jitter) and `Requeue`.

## Refactor suggestions

1. Move the last-used write to a background updater, as with the request log (M2).
2. Introduce `IWebhookClaimStore` plus options-driven batch, lease and parallelism, with a drain loop (M5, m11).
3. Share `RowVersion.TryParse`, `Utc`, `ClientIp` and truncate helpers (see the duplication report).
4. Pre-parse the IP allow-list and scopes into `Known` (m2).
5. Make the limiter's order and comments consistent and add a per-minute ceiling policy (M4, m4).

## Re-review

Not yet done. After fixes, I will re-review only the files touched by M1 to M8 and update this verdict.
