# Code review: M5 Face Recognition

Branch `claude/face-recognition-saas-nv5djm`. Reviewer: Code Review Agent.

**Verdict: REJECT (narrow).** One blocker (B1, idempotency fingerprint) must be fixed, then re-review the changed areas. With B1 and the Majors fixed this is otherwise a solid milestone. The tenancy, billing-in-one-transaction and replay-safety design is good.

Verified: `dotnet build -warnaserror` passes with 0 warnings and 0 errors. The SQL Server integration tests were NOT run (they need Docker). All findings below come from reading the code.

## Blocker

| # | Location | Finding | Fix |
|---|----------|---------|-----|
| B1 | `src/Application/Faces/FaceRecognitionService.cs:455-471` (`Begin`), used by 97, 232, 328 | The server-derived idempotency key is `client + operation + key + image hash`. It omits the request parameters: `ExternalRef`, `ProfileId` and `TopK` (verify/identify), and `ExternalRef`, `ConsentReference`, `DisplayName` and `Metadata` (enroll). The same key and image sent against a different target replays the first result. Verify: key K plus image X against profile A returns Matched, then the same call against profile B returns A's Matched result for free. Enroll: key K plus image X for `emp-1`, then for `emp-2`, replays "Enrolled" and `emp-2` is never created or charged. This contradicts the class comment ("a key can never be used to skip billing for a different request") and returns a wrong verification answer. | Add a canonical request fingerprint to the hash input: normalised `ProfileId` or trimmed `ExternalRef`, `TopK`, and a hash of the enroll fields. Have each operation pass its fingerprint into `Begin`. Add an integration test: same key and image, different profile, must be new work and billed. |

## Major

| # | Location | Finding | Fix |
|---|----------|---------|-----|
| M1 | `FaceRecognitionService.cs:513-523`, `:431-434`, `:141`, `:160`, `:291`, `:370` | There is no `ILogger` in the service. `FaceProviderException` is caught and discarded (`_ = ex;`), so a provider outage leaves no log, no stack trace and no correlation id, only a DB row without the cause. `SkiaImageProcessor.cs:83` also swallows every exception silently. | Inject `ILogger<FaceRecognitionService>`. Use `LogWarning(ex, "Face provider failed for {Operation} client {ClientId} request {RequestId}")` and drop the unused `ex` parameter. Log decode failures at Debug or Warning with no payload. Keep image bytes and embeddings out of logs. |
| M2 | `FaceServices.cs:75-103`, `:118-139` (template index) | Cache memory and stampede. (a) `SizeLimit=2_000_000` counts templates, not bytes. At 512 dimensions (2 KB each) that is about 4 GB per node, and for a 1536-dimension real provider about 12 GB. (b) There is no single-flight on a miss: N concurrent identifies on a cold or invalidated client each load and decrypt the whole gallery, so N copies sit in memory at once and the DB and key service are hit N times. (c) Every enroll, erase or disable invalidates the whole client index, so write-heavy clients get a full reload and decrypt on the next identify. (d) Decryption is a sequential `await` per row. | Size entries as `templates.Count * Dimensions * 4` and set the limit in bytes via options. Use a per-key `SemaphoreSlim` or `Lazy<Task>` (`GetOrCreateAsync` with single-flight). Decrypt in bounded parallel batches. Consider incremental add or remove instead of a full invalidate. Put TTL and limit in `FaceEngineOptions`; they are magic numbers today. |
| M3 | `FaceServices.cs:93-100` and `:120-138` | Stale-write race. A load that read the DB before an enroll commit can call `Set` after `Invalidate` and re-cache pre-enroll data for 60 s on the same node. The caller's own new face is then unidentifiable. `Invalidate` removes the CTS, and `Set` creates a fresh one. | Use a per-client generation counter. Capture it before the DB read and store only if it is unchanged (or tag the entry with it and compare on read). |
| M4 | `FaceRecognitionService.cs:118-121` (profile limit) | `Count >= MaxProfiles` then insert is check-then-act, so concurrent enrolls at `limit-1` all pass and the plan limit is exceeded. It also counts Disabled profiles, which may be intended but is undocumented. | Re-check the count inside the commit transaction under `UPDLOCK, HOLDLOCK` or an `sp_getapplock` keyed by client. Alternatively accept a soft limit and document it. Add a test for the limit. |
| M5 | `FaceRecognitionService.cs:197-206` (re-enroll) | Concurrent re-enrolls of the same `externalRef` load the same old templates and both delete them. The second `SaveChanges` raises `DbUpdateConcurrencyException`, which is translated to `ConcurrencyConflictException`. `CommitAsync` only catches `UniqueConstraintViolationException`, so the whole commit (and charge) rolls back and surfaces as a generic error. The profile `RowVersion` conflict on `RetentionUntil` has the same effect. | Catch `ConcurrencyConflictException` in `CommitAsync`, map it to 409 `CONFLICT` with "retry", and add a test. Do the trim in the DB transaction with a deterministic ordering and tolerate already-deleted rows. |
| M6 | `SkiaImageProcessor.cs:41` and `:48-81`; `IImageProcessor.Prepare` is sync | `Gate.Wait(10s)` blocks a thread-pool thread inside an async request path. Under load, 4 slots plus blocked waiters starve the pool. CPU-heavy decode also runs on the request thread. Memory per request: the full-resolution RGBA decode is up to 25 MP, about 100 MB, then `Orient` and `Downscale` each `Copy()` it again even when no change is needed. That is about 300 MB per slot, times 4, plus 5 MB copies (see m1). | Make the API `Task<Result<PreparedImage>> PrepareAsync`, use `Gate.WaitAsync(timeout, ct)` and `Task.Run` for the decode. Return `source` instead of `Copy()` when no transform is needed. Use `SKCodec.GetScaledDimensions` (decode-time downscale) to avoid full-size decode. Make the gate size and `MaxPixels` options. |
| M7 | `FaceRecognitionService.cs:89-221`, `:225-317`, `:321-403` (SOLID/DRY) | The same pipeline is copy-pasted three times (four with Detect): `Begin`, null-client guard, `Replay`, settings, `Preflight`, `Prepare`, `Stopwatch`, `Detect`, `Classify`, `Fail`, `ProviderFailure` catch, then `CommitAsync`. The "only available to client accounts" string appears 4 times, plus again in `FaceProfileService.Denied`. The class is 589 lines and takes 14 constructor dependencies (SRP smell). B1 above is partly a symptom: the fingerprint has to be threaded through three places. | Extract a `RecognitionPipeline` that takes a small `IRecognitionOperation` strategy (replay mapper, pre-checks, post-engine step, commit extras). Split enroll, match (verify and identify) and detect into collaborators. Share one `ClientOnly` error constant. |

## Minor

| # | Location | Finding | Fix |
|---|----------|---------|-----|
| m1 | `FacesController.cs:118-121` | The upload is copied three times: `IFormFile` buffer, `MemoryStream`, then `ToArray()`. `SKData.CreateCopy` makes a fourth, and the hash is computed on a fifth pass. | `var bytes = new byte[image.Length]; await stream.ReadExactlyAsync(bytes, ct);`. Take `ReadOnlyMemory<byte>` in the service. |
| m2 | `FacesController.cs:20, 108`; `SkiaImageProcessor.cs:17, 33` | The 5 MB limit is defined three times (6 MB request, 5 MB controller, 5 MB processor), plus the string "5 MB" twice. | Single `FaceLimits.MaxImageBytes` constant (Application or Contracts), or `FaceOptions`. |
| m3 | `FaceRecognitionService.cs:578-587` | The unique-violation recovery is too coarse. If the idempotency key now exists, the winner is already committed (the loser waited on the index lock), so "is being processed" is wrong. It should return the winner's stored result via replay. Any other unique violation (for example a `MatchResults` PK) is mislabeled `DuplicateExternalRef`. | Call `ReplayAsync` and map it. Inspect the violated index name (`SqlException.Message`, or expose the constraint in `UniqueConstraintViolationException`). |
| m4 | `FaceRecognitionService.cs:513-523` | `ProviderFailureAsync` is not defensive: `SaveChangesAsync(CancellationToken.None)` outside any try, so a DB error masks the 503. It also writes one row per failed call, which is unbounded growth during an outage. | Wrap in try/catch and log. Consider sampling or capping provider-error rows. |
| m5 | `FaceRecognitionService.cs:165-181` | `FaceProfile.Create` (domain validation) runs after the engine and encryption work; the `DomainException` path is late. The validator already covers these rules, so the duplication is dead weight. | Validate before `Prepare` (cheap checks first) and keep one rule source. |
| m6 | `FaceRecognitionService.cs:193-206` | Re-enroll silently ignores `ConsentReference` and `Metadata`. A new consent reference is dropped, which is a compliance gap (`ConsentRecordedAt` never updates). | Update the consent reference and `ConsentRecordedAt` (add a domain method), and apply metadata. Test it. |
| m7 | `FaceRecognitionService.cs:376-380` | `Scoring.Rank` does a LINQ `GroupBy` and full sort over every template, allocating a tuple and group per template. Only `topK` is needed. It then runs an extra `ExistingTemplateIds` round trip. | Keep a best-per-profile dictionary and a `topK` heap (use `PriorityQueue`). Compute similarity in a loop with no LINQ allocation. |
| m8 | `FaceRecognitionService.cs:391-394` | Identify persists every ranked candidate (up to 20 `MatchResults` per call), including non-matches. This is write amplification on the hot path. | Persist matches plus the best non-match only, or cap via a setting. |
| m9 | `FaceRepositories.cs:17-21` | `GetProfileAsync` and `GetProfileByExternalRefAsync` are tracked for read-only callers (Verify, Get, Detail). | Add `AsNoTracking` read variants (`FindForUpdate` vs `Find`). |
| m10 | `FaceProfileService.cs:163-170` | `DeleteTemplateAsync` loads all templates of the profile and filters in memory. It also skips the profile-existence and ownership check by relying on the tenant filter. | `GetTemplateAsync(profileId, templateId)` with a SQL predicate. |
| m11 | `FaceProfileService.cs:114`, `:119` | `DateTime.SpecifyKind(until, Utc)` mislabels a Local or Unspecified value. Status strings `"Disabled"` / `"Active"` are magic strings duplicated with the validator. There is no check that `RetentionUntil` is not in the past. | Normalise with `ToUniversalTime()` when Kind is Local, and reject Unspecified. Parse `Status` into the enum once, in the validator or DTO. |
| m12 | `FaceRepositories.cs:41-42`, `FaceConfigurations.cs` | Index coverage gaps. (a) The profile list orders by `CreatedAt` per client and no index serves it (`(ClientId, Status)` does not). (b) `ListAsync` with `profileId` uses `TargetProfileId == pid OR Matches.Any(...)`, which cannot seek. (c) `(ClientId, ProfileId)` on FaceTemplates and the single `(ClientId, Provider, ModelVersion, Status)` index are fine, but `ImageSha256` has no uniqueness guarantee, so the duplicate-image check is check-then-act. (d) `RecognitionRequests` has no retention or archival. | Add `(ClientId, CreatedAt DESC)` on FaceProfiles. Rewrite the profile filter as a `UNION` or an `EXISTS` over `MatchResults(ClientId, ProfileId)`. Decide the duplicate-image semantics explicitly. Add a history retention sweep. |
| m13 | `FaceRetentionSweeper.cs:61` | Hard-coded batch of 200 per client per hour, so a backlog drains slowly. Biometric retention should drain fully (loop until 0, bounded). Two nodes sweep the same clients and race into concurrency exceptions that are logged at Error. `RecognitionRequests.InputImageSha256` outlives the erased profile; confirm that is acceptable under the erasure policy. | Loop until the batch comes back empty, with a cap. Log `ConcurrencyConflictException` at Debug. Move the interval and batch to options. |
| m14 | `FacesController.cs:38` | Enroll returns `Ok(r)` while the other endpoints use the default mapping. Creation of a resource arguably should be 201 with a Location. | Align with the licensing and tenancy controllers' conventions. |
| m15 | `FaceServices.cs:14-45` | `FaceEngineOptions.Provider` is validated but never used to select the engine: `AddSingleton<IFaceEngine, MockFaceEngine>()` is hard-wired. The "abstraction for real variation" is half-wired. | A small `IFaceEngineFactory` or keyed registration by `Provider`. |
| m16 | `FaceRecognitionService.cs:85` and `:569` | `Now` is read again inside the transaction, giving different timestamps for the same operation (request `CreatedAt` vs template `CreatedAt`). | Capture once in `BeginContext`. |
| m17 | `Abstractions.cs:6` | `PreparedImage.Sha256` is the hash of the raw input, but the service recomputes it in `Begin`. The field is unused in production and only asserted in a test. | Remove it, or use it and drop the hash in `Begin`. |

## Tests

| # | Location | Finding |
|---|----------|---------|
| t1 | `FacesTests.cs:266-287` | The idempotency test would not catch B1: there is no case with the same key and image but a different profile or `externalRef`. Add one for verify and one for enroll. |
| t2 | `FacesTests.cs:289-302` | The concurrent-key test is weak: it accepts any mix of 200/409 with `>= 1` OK. It never asserts that the 409s are retry-safe or that a retry then replays. Assert exactly one `RecognitionRequest` row, then replay the key and expect the same `RequestId`. It also exercises the unique-violation path only if timing happens to collide, so it can pass without hitting the catch. |
| t3 | `FacesTests.cs:177-188` | The five-template cap asserts only the count. It does not assert that the newest five survive (oldest deleted) or that the profile has exactly one template on first enroll. |
| t4 | missing | No test for: concurrent enroll of the same `externalRef` (expect one profile, one 200, the other 409 `DUPLICATE_EXTERNAL_REF` and not charged); `PROFILE_LIMIT_REACHED` (`maxProfiles`) or a plan limit; `topK` (request override, default, and 21 rejected); `PROFILE_HAS_NO_TEMPLATE`; a provider outage on enroll and verify (only identify is covered); a charge refused inside `FailAsync`; a failed commit leaving no profile or template; cache invalidation on disable, template delete and update; `FaceRetentionProcessor` as a unit; the profile `UpdateAsync` paths; history filters; and replay of enroll and identify. |
| t5 | `FaceEngineTests.cs:109` | Pointless `async Task` plus `await Task.CompletedTask` in a synchronous test. The `Embed(Pattern(1))` call is computed twice. |
| t6 | `FaceEngineTests.cs:83-84` | Thresholds (`> 0.99`, `< 0.5`) depend on SkiaSharp JPEG encode and resize output, which can vary across platform and library versions. Use margins relative to the mock's known behaviour, or seed from raw bitmaps. |
| t7 | `FacesTests.cs:343` | Mixes `DateTime.UtcNow` with the service's injected `TimeProvider`. Prefer the fake clock if the factory exposes one. |
| t8 | all | There is no unit-level test of `FaceRecognitionService` with fakes. Every branch (Classify, ordering, charge failure, replay mapping) can only be reached through Docker-backed tests, so feedback is slow and coverage of the error branches is thin. Add a fake-based unit suite alongside the integration ones. |

## Duplication report

- Pipeline preamble and tail repeated in `EnrollAsync`, `VerifyAsync`, `IdentifyAsync` and `DetectAsync` (see M7). Roughly 60 duplicated lines.
- The "only available to client accounts" error is built in five places.
- The 5 MB limit is defined in the controller, the processor and a test (see m2).
- Name encrypt/decrypt with purpose `"face-profile-name"` appears as a literal in `FaceRecognitionService:167` and as `NamePurpose` in `FaceProfileService`. Share one constant, or move it into a name codec (like `EmbeddingCodec`).
- The metadata rules (4096, 50 keys) are in `MetadataRules` and again in the DB column length and the validator message.
- The `Status` string-to-enum mapping is repeated in the validator and the service.

## Positive notes

- The charge and the attempt record are in one transaction, and the idempotency unique index rolls back the loser's deduction.
- Commit is never cancelled mid-flight (`CommitAsync(CancellationToken.None)` in `ExecuteInTransactionAsync`).
- The licence pre-flight runs before image work.
- Tenant filters and strict tenancy apply to all face entities.
- Embeddings and names are encrypted per client.
- The decompression-bomb guard, magic-byte checks and re-encode in the image processor are well done.
- `TimeProvider` is used consistently.
- No retrying execution strategy is enabled, so the non-idempotent transaction delegate is currently safe. Document that coupling in `CommitAsync`, because the delegate re-adds tracked entities.

## Refactor suggestions

1. Introduce the shared pipeline (M7), with the request fingerprint (B1) as part of the context.
2. Replace the template cache with a size-in-bytes, single-flight, generation-checked store (M2, M3).
3. An async `IImageProcessor` that decodes with a scaled codec (M6).
4. A `FaceOptions` section for limits: image bytes, pixels, gate size, cache TTL, cache bytes, sweep batch, interval.

## Not run

- SQL Server integration tests (`FacesTests`): need Docker.
- Unit tests were not executed (build only).
