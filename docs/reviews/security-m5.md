# Security Review - M5 Face Recognition

Reviewer: Security Agent (independent) - Baseline: docs/04-security-strategy.md (T1-T12, section 9 biometric privacy)
Branch: claude/face-recognition-saas-nv5djm (commit 06a1169)
Method: code reading only. The SQL Server integration tests were deliberately not run (Docker needed). No product code changed.

## Verdict: FAIL (1 High open) - becomes PASS-WITH-CONDITIONS once H-1 is fixed and re-verified

Tenant isolation, encryption, billing atomicity and authorization are solid. The blocking issue is availability: the image-processing gate can starve the whole API, across tenants.

## Tool results
| Tool | Result |
|---|---|
| `dotnet list package --vulnerable --include-transitive` | Ran. No vulnerable packages in any project (SkiaSharp 3.119.2). |
| gitleaks | Not installed. Could not run. |
| `IgnoreQueryFilters` grep in src | Ran. No occurrences anywhere in src. |
| Integration tests (SQL Server) | Not run, by instruction. |

## Findings

| ID | Sev | Location | Summary |
|---|---|---|---|
| H-1 | High | `src/Infrastructure/Faces/SkiaImageProcessor.cs:22,41-44,66-75`; `Api/Startup/ServiceCollectionExtensions.cs:108-114` | Blocking gate plus large decode: cross-tenant DoS |
| M-1 | Medium | `FaceRecognitionService.cs:112-126,174-208` | Enrolling onto an existing profile adds a template with no consent check or extra permission (identity takeover by a low-privilege insider) |
| M-2 | Medium | `FaceRecognitionService.cs:315`, `FaceDtos.cs:15,19` | Raw similarity score returned for non-matches: hill-climbing oracle |
| M-3 | Medium | `FaceProfileService.cs:112-115,150,292`; `FaceConfigurations.cs:47-68`; `FaceRecognitionService.cs:200` | Erasure incomplete (audit keeps ExternalRef, history keeps image hash and IP forever); retention can be extended without a cap |
| M-4 | Medium | `FaceServices.cs:118-139,75-91`; `FaceRepositories.cs:50-53` | Identify cache: unbounded load, no single-flight, 1 KB or more per template (memory/CPU DoS) |
| L-1 | Low | `FaceRecognitionService.cs:97-103,232-241,328-339` | Replay path skips profile existence and active checks |
| L-2 | Low | `FaceServices.cs:93-100`, `FaceServices.cs:85-90` | Cache invalidation race (ObjectDisposedException, stale set) |
| L-3 | Low | `FaceServices.cs:21-45` | Mock guard keyed to the "Production" name only; mock accepts look-alike images |
| L-4 | Low | `FacesController.cs:118-121` | Biometric uploads spooled to temp files and copied three times |
| L-5 | Low | `FaceConfigurations.cs:39`, `FaceRepositories.cs:55-56` | Duplicate-image check is TOCTOU (no unique index) |
| L-6 | Low | `FaceProfile.cs:26`, `FaceValidators.cs:46-87` | Metadata stored in plaintext (DisplayName is encrypted) |
| L-7 | Low | `FaceRetentionSweeper.cs:61`, `FaceProfileService.cs:283` | Sweeper capped at 200 profiles per tenant per hour; backlog delays erasure |
| L-8 | Low | `EmbeddingCodec` (`FaceServices.cs:62-71`) | One corrupt or shredded template makes Identify return 500 for the whole tenant; embedding AAD has no row binding |
| I-1 | Info | - | Verified controls (below) |

### H-1 Image gate blocks threads; large decodes amplify memory (High, T10/T7)
- Evidence:
  - `Gate.Wait(TimeSpan.FromSeconds(10))` (line 41) is a synchronous wait inside a request thread. The semaphore has 4 slots (line 22).
  - The pixel cap is 25 MP (line 18). A 25 MP RGBA decode is about 100 MB. The code then makes `Orient` (always `source.Copy()`, line 115) and `Downscale` (also copies or resizes) at once, so each slot holds roughly 200-300 MB. Four slots is over 1 GB.
  - A tiny (about 100 KB) flat PNG at 5000x5000 passes the 5 MB byte cap and still costs full decode CPU and memory.
  - The only rate limit is 300 requests/min per IP (`HostingOptions.cs:39`). There is no per-client or per-key limit and no limit on queued waiters.
  - `Detect` is free by default and still runs the full pipeline.
- Attack path:
  1. A malicious tenant, or one leaked API key with `faces.detect` or `faces.verify`, sends concurrent uploads of 25 MP PNGs.
  2. Four decodes hold every slot. Every other request blocks a thread-pool thread for up to 10 s.
  3. About 5 requests/s per IP means dozens of blocked threads. The thread pool grows slowly, so login, other tenants' calls and health probes starve. Memory pressure can OOM-kill the container.
- Impact: platform-wide outage caused by one tenant.
- Fix:
  - Use `await Gate.WaitAsync(timeout, ct)` and reject with 503 + `Retry-After` when the gate is full. This is the behaviour docs/04 section 8 promises.
  - Cap the gate queue.
  - Lower `MaxPixels` to about 12 MP.
  - Decode with `SKCodec.GetPixels` using a scaled (`GetScaledDimensions`) target, or downscale first, so the full RGBA bitmap is never held.
  - Avoid the redundant `Copy()` calls.
  - Add a per-client (credential) limiter on `/faces/*`.
  - Bound the whole pipeline with a total request timeout.
  - Consider running Skia out of process. Native Skia crashes cannot be caught (`catch` at line 83 is irrelevant for them).
- Re-verify: a test with N parallel 25 MP images must show unrelated endpoints (for example `/health`) staying responsive.

### M-1 Template added to an existing profile without consent or privilege (Medium, T1/T6)
- Evidence:
  - When `existing` is non-null, `EnrollAsync` only requires `faces.enroll`. `ConsentReference` is validated but ignored, so the stored consent never changes.
  - The new template is appended, `DisplayName` is overwritten, and `RetentionUntil` is reset (lines 195-205).
  - `ClientUser` can be granted `faces.enroll` (docs/04 section 3).
- Attack path: an insider or leaked key with `faces.enroll` enrolls their own face under the `externalRef` of a privileged person. Later `verify` for that person succeeds with the attacker's face. The credit cost is 1.
- Fix:
  - Add a separate permission (for example `faces.enroll-additional`), or require `faces.manage` to add a template to an existing profile.
  - Optionally require an explicit `addTemplate=true` flag, or a successful verify against an existing template.
  - Record consent per template, or reject a different `ConsentReference`.
  - Audit the add (the audit entry exists but does not flag it as an addition).

### M-2 Score oracle (Medium, T6)
- Evidence: `VerifyFaceResponse` always returns `Score` (rounded to 4 decimals), even for `NoMatch`. `IdentifyFaceResponse.BestScore` is also returned on `NoMatch`.
- Attack path: with an API key, iterate image perturbations and keep the highest score (a standard hill-climbing or synthesis attack) to craft an image that passes against a victim's profile. The only brake is 300 requests/min per IP.
- Fix:
  - Return the score only to callers with a dedicated permission, or quantise it, or omit it on `NoMatch`.
  - Add per-profile and per-credential attempt throttling for failed verifies, with lockout and alerting.

### M-3 Erasure and retention completeness (Medium, docs/04 section 9)
- Evidence:
  - `FaceProfileService.cs:150` and `:292` write `OldValues: { ExternalRef }` to the append-only audit log. This contradicts "audit entry keeps only ids" (docs/04 section 9). `ExternalRef` is often an e-mail or national id, and it survives erasure forever.
  - `RecognitionRequests` keep `InputImageSha256`, `IpAddress`, `TargetProfileId` and `MatchResults.ProfileId` indefinitely. The sweeper never trims history. The photo hash lets someone later confirm that a given photo was processed.
  - `UpdateAsync` (line 112) accepts any `RetentionUntil`, with no validator rule, no cap tied to `face.retentionDays` (max 3650) and no platform maximum. The `RetentionUntil` clearing branch is absent, but an arbitrary far-future date is allowed. `faces.manage` therefore bypasses the retention policy.
  - Re-enrolling resets `RetentionUntil` (line 200).
- Fix:
  - Audit only the profile id, or a salted HMAC of `ExternalRef`.
  - Add a history retention policy: null or hash the `InputImageSha256`/IP of erased profiles, or purge rows by age.
  - Validate `RetentionUntil <= now + settings max` and not in the past by more than a small tolerance.
  - Do not extend retention on re-enroll unless the consent says so.

### M-4 Identify memory and CPU (Medium, T10)
- Evidence:
  - `TemplateIndex.GetAsync` loads and decrypts every active template of the tenant on each cache miss. There is no single-flight, so concurrent misses each decrypt everything.
  - The TTL is 60 s, so each tenant reloads every minute.
  - `SizeLimit = 2_000_000` counts templates, which is about 2 GB at 256 floats and 4 GB at 512 floats, per node.
  - `Scoring.Rank` runs on the request thread. With `MaxProfiles` defaulting to 10000 and 5 templates each, one identify is 50k similarity computations.
- Fix:
  - Single-flight the load (`Lazy<Task>` or `SemaphoreSlim` per client).
  - Set the size limit in bytes.
  - Cap templates per tenant, or move to a vector index.
  - Run `Rank` with bounded parallelism.

### L-1 Replay returns stale references (Low)
Replay runs before preflight, which is by design and billing-safe. Cross-tenant leakage is impossible: the key includes `clientId`, the unique index is `(ClientId, IdempotencyKey)`, the lookup passes through the strict filter and RLS applies. But replays do not re-check profile state:
- Verify and Enroll replays return the `ProfileId`/`TemplateId` of an erased profile.
- Identify replay filters on existence only (`refs.ContainsKey`), not on `Active`, so a disabled person is still returned.

Fix: apply the same alive/active filter on replay.

### L-2 Cache races (Low)
`Invalidate` disposes the `CancellationTokenSource` after `TryRemove`, while a concurrent `Set` may already hold it from `GetOrAdd`. Reading `cts.Token` then throws `ObjectDisposedException` (500). A load that started before an invalidate can also `Set` stale data after it. The Identify DB re-check (`ExistingTemplateIdsAsync`, which also checks profile Active) bounds this for security. The cost is that erased entries consume top-K slots, so a false `NoMatch` is possible. Fix: use a version counter per client in the cache key instead of disposing tokens, and re-check candidates before the top-K truncation.

### L-3 Mock engine guard (Low)
The guard is correct for `IsProduction()` and is validated on start, with `ValidateOnStart` (`DependencyInjection.cs:130`). Gaps:
- A Staging or custom-named environment holding real data silently runs the mock.
- `MockFaceEngine` is registered unconditionally.
- The mock treats a 16x16 brightness thumbnail as a face, so unrelated look-alike images can pass at the default threshold of 0.60.

Fix: allow the mock only when the environment is Development or Testing, or when the flag is set. Log a startup warning and expose the provider name in a health or response header.

### L-4 Temporary files and copies (Low)
Multipart file sections larger than the buffer threshold are spooled to the OS temp directory unencrypted (`IFormFile`). `WithImage` then adds a `MemoryStream` copy plus `ToArray()` (about 15 MB per 5 MB upload). Raw bytes are not zeroed. Fix: point `TMPDIR` at an encrypted or tmpfs volume, stream into a pooled buffer sized to the limit, and zero buffers after use.

### L-5 / L-6 / L-7 / L-8 (Low)
- L-5: there is no unique index on `(ClientId, ImageSha256)`, so concurrent enrolls of the same image can both succeed. This is harmless to security but not guaranteed.
- L-6: `MetadataJson` is plaintext at rest. The validator permits long string values within the 4 KB budget, and does not check for control characters. Document that it must not contain PII, or encrypt it. Any future UI must render it as text, never `MarkupString`.
- L-7: `ProcessAsync(200)` once an hour means 10k expired profiles take 50 hours to purge. Loop until the batch comes back short.
- L-8: one undecryptable template (corrupt, or after the client key is destroyed) throws in `TemplateIndex.GetAsync` and breaks Identify for the entire tenant with a 500. Skip and log the row. Consider adding the template id to the AAD to stop intra-tenant ciphertext transplant (low value).

## Verified controls (no finding)
- **Tenant isolation.**
  - All four tables implement `IStrictTenantOwned`. The EF filter is `ClientId == FilterClientId` with no platform bypass (`AppDbContext.cs:260-267`).
  - The write guard throws for platform scope (`AuditAndTenantSaveChangesInterceptor.cs:113`).
  - RLS uses the strict function for these tables, with filter and block predicates for insert, update and delete, built from the model (`RowLevelSecurityScriptBuilder.cs`).
  - There is no `IgnoreQueryFilters` in the M5 code.
  - `ClientId` comes only from `ICurrentUser`. The request DTOs contain no `ClientId` (no over-posting).
  - Cross-tenant ids return 404 (repository lookups pass through the filter).
  - Platform users get 403 `Denied` because they have no `ClientId`; platform roles have no `faces.*` permission (`Permissions.cs` `PermissionsFor`).
- **Sweeper.** It lists client ids in platform scope, then runs `BeginTenant(clientId)` per client in its own DI scope. Erasure goes through the tenant-filtered, RLS-protected context. Failures are isolated per client.
- **Encryption.** AES-256-GCM, random 96-bit nonce, per-client DEK wrapped by the KEK. AAD binds clientId, purpose (`face-template`, `face-profile-name`) and key version. Templates and display names cannot be swapped across tenants or purposes. Embeddings are never returned (see test `Biometric_data_never_appears_in_responses`). Erase removes templates by DB cascade.
- **Billing.**
  - `CommitAsync` charges and writes the request row in one transaction. A failed charge returns an error with nothing persisted, so the result is withheld.
  - A unique-index race rolls the deduction back with the failed insert.
  - The idempotency key is derived server-side from `clientId|operation|key|imageSha`, so a caller cannot reuse a key to skip billing for other work.
  - Provider failures are never billed or replayed.
  - The deduction is the atomic conditional update (`TryConsumeAsync`).
- **Image handling.**
  - Type is decided from magic bytes (JPEG, PNG, WebP) and byte size is checked before decoding. Dimensions come from the header only (`SKCodec`) and the pixel cap is enforced before decode.
  - The image is always re-encoded to JPEG, so metadata and polyglot trailers are dropped.
  - Nothing is written to disk by the app, and no user-controlled paths exist.
  - Skia exceptions are mapped to generic 4xx errors.
- **AuthZ mapping.** Every action has `[HasPermission]`, and the fallback policy requires authentication. Enroll/Verify/Identify/Detect, Read, Manage, Erase and History map to their own keys. Auth is bearer or API key only (no cookie scheme), so there is no CSRF exposure on these multipart POSTs.
- **Input validation.**
  - `ValidationFilter` runs the validators for form-bound DTOs (lengths, the XOR of `ProfileId`/`ExternalRef`, TopK 1-20, metadata flat JSON at most 4 KB and 50 keys, status enum).
  - `Idempotency-Key` is capped at 100 characters, then hashed.
  - The profile search `LIKE` escapes `%`, `_`, `[` and the escape character (no injection). There is no raw SQL.
- **Logging.** No Face code logs biometric data, images, scores or ExternalRef. The sweeper logs only counts and ClientId.
- **Enumeration.** `externalRef` existence differences (404 vs 409 vs 422 and faster response when unknown) are visible only inside the caller's own tenant. Every holder of `faces.verify` already has `faces.read` in the default roles. No cross-tenant oracle was found.

## Required before sign-off
1. H-1 fixed and re-verified by the Security Agent.
2. M-1 to M-4: fix, or accept in writing with a ticket.
3. Add tests: parallel large-image load keeps the API responsive; add-template requires the higher permission; retention bounds enforced; audit/history of an erased person contains no `ExternalRef` or image hash.
4. Add gitleaks to CI (not available locally).
