# 07 — M14 Digital Verification Platform (Bank Statement, PAN, Passport — release 1)

> Owner: Solution Architect Agent · Status: **Design v1; backend built (contract skeleton, persistence, processors, submission/worker/API/admin: see §18); UI, Security review, QA accuracy pass and DevOps pending** · Binding requirements: [`requirements/m14-document-verification.md`](requirements/m14-document-verification.md) (referred to as "REQ §n").
> Companion deltas (all marked "M14 (planned)"): `01` §12, `02` "M14 additions", `03` §7d, `04` §14, `05` M14 row, `06` §6, `STATUS.md`.

Reading guide: sections 1–4 are the architecture; 5–8 the processing, outcome and credit rules; 9–11 data, API and UI; 12–14 risks, tests, ADRs; 15–17 questions, risks and the work plan with file ownership.

---

## 1. Assessment of reusable capabilities (read from the code at tip `169badb`)

| Need | What exists (file) | Verdict |
|---|---|---|
| Image validation / preparation | `Infrastructure/Faces/SkiaImageProcessor.cs` (magic bytes, pixel cap, bounded decode gate with 503 load-shedding, re-encode to JPEG, metadata stripped), `Application/Faces/Abstractions.cs` (`IImageProcessor`, `PreparedImage`). SkiaSharp `3.119.2` + `SkiaSharp.NativeAssets.Linux.NoDependencies` already pinned in `Directory.Packages.props`. | **Reuse the package and the pattern, not the class.** It is face-specific (downscales to a bounded JPEG, 5 MB). OCR needs full-resolution grayscale pages. New `IDocumentImagePreprocessor` (Infrastructure) copies the gate/pixel-cap idea. Do not widen `IImageProcessor`. |
| File / blob storage | **None.** `docs/01` mentions `IBlobStore`/`FileSystemBlobStore` but no such type exists (grep: no hits). Face images are never retained. | **Gap → new `IDocumentStorageService`** (ADR-19). |
| Encryption with per-client keys | `Application/Abstractions/IClientEncryption.cs` (AES-256-GCM under the calling client's data key, `purpose` label bound, `DestroyKeysAsync` = crypto-shredding); `IPlatformCrypto` (`Protect`, `ComputeMac` per purpose, keyring aware). | **Reuse as is.** Documents and extracted data are sealed with `IClientEncryption`; dedupe hashes use `IPlatformCrypto.ComputeMac("verification-subject", …)`; download grants use random tokens (hash stored). Key rotation (`rotate-master-key`) already covers wrapped data keys; no change. |
| Credit consumption | `Application/Licensing/LicenseMeteringService.cs` (`PreflightAsync`, `ChargeAsync(ChargeCommand)` = FEFO, single conditional `UPDATE` via `IMeteringStore.TryConsumeAsync`, ledger append via `LedgerWriter`, idempotency key unique per `(ClientId, IdempotencyKey)`, replay returns the stored row); `LicenseService.RefundAsync` (admin refund as a new ledger row, unique `ReferenceTransactionId`). | **Reuse unchanged.** Needs: new `MeteredOperation` members; a way to link the ledger row to the verification request (see §8.5, reuses the existing `RecognitionRequestId` column). |
| Cost rules | `Domain/Licensing/CostRule.cs`, `ClientCostRule`, `Application/Licensing/CostRuleResolver.cs` (client override → plan → platform default → `Fallback`, versioned by `EffectiveFrom`), admin UI `CostRuleList`. Rules are keyed by `MeteredOperation`; the enum is stored as string with a CHECK constraint. | **Reuse.** Add one operation per billable stage (§8.2). Fallback of 1 credit for unknown ops is a safety net only; seed explicit defaults. |
| Idempotency | Faces: server-derived key `SHA256(client|operation|callerKey|imageSha|fingerprint)` + unique filtered index on the request row (`FaceConfigurations`: `(ClientId, IdempotencyKey)`), losing racer rolled back (`FaceRecognitionService.Begin/ReplayAsync/CommitAsync`). | **Reuse the pattern** (same derivation, same unique index shape). |
| AuthN/Z, permission catalogue | `Contracts/Identity/Permissions.cs` (catalogue + `SystemRoles.PermissionsFor`: Super Admin = all non-client keys, Client Admin = all non-platform keys, Client User = explicit list), `Api/Authorization/PermissionAuthorization.cs` (`[HasPermission]`), API-key scopes = subset of creator's permissions, JWT + API key both accepted on `/api/v1/faces`. | **Reuse.** New keys are constants + one `All` entry each (§10.1). |
| Tenant ownership / RLS | `Domain/Common/ITenantOwned.cs`, `IStrictTenantOwned` (platform sees nothing), `IAppendOnly`; `TenantModelRules`; RLS script generated from the model; `ITenantScope.BeginTenant/BeginPlatform` for jobs; `IgnoreQueryFilters` confined by architecture test. | **Reuse.** Request metadata `ITenantOwned`; documents/results/checks `IStrictTenantOwned` (§9). |
| Audit | `Application/Auditing/IAuditService.cs` (`Record(AuditEntry)` staged in the current unit of work, redaction of secrets). | **Reuse.** Own action-name constants in the module (no edit of `AuditActions`). |
| Background jobs | `Infrastructure/Background/EmailDispatcher.cs` + `Platform/EmailStore` (claim with `UPDLOCK, READPAST` under a lease, backoff + jitter, shutdown hand-back, `PeriodicTimer`), `WebhookDispatcher` (per-endpoint fairness, lease validation at start-up), `FaceRetentionSweeper` (lists clients in platform scope, works inside each tenant scope, batch-drains), `PublicDataPurger`. | **Reuse the patterns** for `VerificationWorker` and `VerificationRetentionSweeper`. |
| Rate limiting | `ThrottleAttribute` + `IPrincipalThrottle` (`ThrottlePolicies`, `ThrottleOptions`, shared SQL counters), per-key rate limit + daily quota, `RequestLimitsOptions` (6 MB default body, slow-loris floor 240 B/s, 60 s request timeout). | **Reuse; extend** with two policies and a per-route larger body limit (`[RequestSizeLimit]` as `FacesController` does). |
| CSV / admin dashboards | `Api/Http/CsvStreamResult.cs` (streaming, BOM, injection neutralisation), `Application/Dashboards/*`, `Infrastructure/Persistence/Queries/DashboardQueries.cs` (aggregate by ledger operation), `[Throttle(Exports)]`. | **Reuse.** Dashboards group by `Operation`; Backend must check they tolerate the new operations (they currently describe "recognitions"). |
| Webhooks | `Domain/Api/Webhooks.cs` (`WebhookEvents`), `IWebhookPublisher` used inside the commit transaction by `FaceRecognitionService`. | **Reuse**; add event `verification.completed` (needed because processing is async). |
| Typed outbound HTTP | `Infrastructure/DependencyInjection.cs` (`AddHttpClient<…>` + `ConfigureProviderClient`), `Infrastructure/Billing/ProviderSupport.cs`, `Webhooks` SSRF guard (https, public IPs, no redirects). No resilience package (`Microsoft.Extensions.Http.Resilience`/Polly) is referenced. | **Reuse the pattern.** Retries happen at job level (§7) so no new package is required (ADR-26). |
| PDF / OCR | **None.** No PDF library, no OCR engine anywhere in `src/` or `Directory.Packages.props`. | **Gap** (§6). |
| Blazor patterns | `Services/NavigationCatalog.cs` + `NavModels.cs` (`NavGroup`, `NavItem(…, RequiredPermission, Implemented)`; `Trim` by permission; `Implemented:false` shows "Soon"), `WebPermissions.cs`, typed clients in `Services/Api/*`, shared components (`PageHeader`, `DataTable`, `FilterBar`, `StatusChip`, `StatCard`, `ChartCard`, `ConfirmDialog`, `ErrorState`, `EmptyState`, `SkeletonTable`, `DetailList`, `ClientPicker`, `FacePhotoInput` pattern for uploads, `ImageSniffer`, `Can`, `CodeSample`), `ApiCallOptions.Passive`, BFF download relay + export throttle. | **Reuse.** New components listed in §11. |
| Tests | `tests/TestSupport` (`SqlServerFixture`, `TestDb`, `FakeSmtpServer`), `tests/Api.IntegrationTests/Support/ApiFactory.cs` (`WebApplicationFactory<Program>` on a Testcontainers SQL Server), bUnit `Web.ComponentTests`, `Architecture.Tests/DependencyRuleTests.cs`. | **Reuse.** Fakes live in `tests/TestSupport/Verification` (§5.4). |

Gaps to fill: document storage, PDF inspection/text/rasterisation, OCR, document image preprocessing, MRZ/PAN/name/bank-statement logic, the verification entities, async job pipeline, download grants, module registry, new metered operations.

---

## 2. Module boundaries and layering

Dependency rule unchanged: Domain ← Application ← Infrastructure ← Api; Web depends on Contracts only. New feature folder `Verification` in every layer (ADR-01 modular monolith, feature-sliced).

### 2.1 What goes where

| Layer / folder | Types |
|---|---|
| **Domain** `src/Domain/Verification/` | `VerificationRequest` (aggregate, `AuditableEntity`, `ITenantOwned`; owns the state machine, §7.3), `VerificationDocument` (`IStrictTenantOwned`), `VerificationDocumentBlob` (`IStrictTenantOwned`), `VerificationResult` (`IStrictTenantOwned`), `VerificationCheck` (`IStrictTenantOwned`), `VerificationEvent` (`ITenantOwned`, `IAppendOnly`), `VerificationDownloadGrant` (`ITenantOwned`), `VerificationModuleSetting` (global, no `ClientId`). Enums: `VerificationType` {BankStatement, Pan, Passport}, `VerificationStatus`, `StageState`, `ExternalState`, `CheckOutcome`, `CheckSeverity`, `CheckStage`, `FieldMatchOutcome`, `AssuranceLevel`, `DocumentKind`, `SubmitSource`. Pure rules that need no I/O (state transitions, `MarkExternal` invariant) live here. `Domain/Licensing/Enums.cs`: new `MeteredOperation` members (§8.2). `Domain/Api/Webhooks.cs`: event `verification.completed`. |
| **Contracts** `src/Contracts/Verification/` | Request DTOs (`SubmitBankStatementRequest`, `SubmitPanRequest`, `SubmitPassportRequest`), response DTOs (§10.4), `VerificationModuleKeys` (string constants), `VerificationQuery` records, enums as strings. `ErrorCodes` additions (§10.5). `Permissions` additions (§10.1). No behaviour. |
| **Application** `src/Application/Verification/` | `Abstractions/` (all interfaces in §3 and §4), `Modules/` (registry, descriptors, upcoming catalogue), `Submission/` (`VerificationSubmissionCore`, three module services, validators), `Processing/` (`VerificationProcessor`, stage runner, `VerificationOptions`), `Query/` (`VerificationQueryService`, report builder, download grants), `Admin/` (admin services), `Billing/` (`VerificationMeter`, thin wrapper over `ILicenseMeteringService`), `BankStatements/` (normalised model, `IBankStatementParser`, rule sets, checks), `Pan/` (rules, holder-type table), `Passport/` (MRZ TD3 parser, rules), `Matching/` (name normaliser/matcher). All of these are pure or I/O through interfaces. |
| **Infrastructure** `src/Infrastructure/Verification/` | `Storage/SqlDocumentStorageService`, `Documents/` (`PdfPigDocumentInspector`, `PdfPigTextExtractor`, `PdfiumRasterizer`, `SkiaDocumentImagePreprocessor`, `DocumentParser`), `Ocr/TesseractCliOcrProvider` (+ `NullOcrProvider`), `Providers/NotConfiguredPanVerificationProvider`, `NotConfiguredPassportVerificationProvider`, `ProviderCircuitBreaker`, `Persistence` is owned by the Database agent under `Infrastructure/Persistence/{Configurations,Repositories,Queries}/Verification*` , `Platform/VerificationStore` (claim/lease SQL, like `EmailStore`), `Background/VerificationWorker`, `Background/VerificationRetentionSweeper`, `VerificationInfrastructureRegistration.cs`. |
| **Api** | `Controllers/Faces`-style: `Controllers/Verification/BankStatementVerificationController`, `PanVerificationController`, `PassportVerificationController`, `VerificationsController` (modules, list, status, result, transactions, cancel, retry-external, erase, downloads), `Controllers/Admin/VerificationAdminController`. Thin: bind → `[HasPermission]` → `[Throttle]` → one service → `ToActionResult`. |
| **Web** `src/Web/Blazor/` | Pages, components, `Services/Api/VerificationApiClients.cs` (`IVerificationApiClient`, `IVerificationAdminApiClient`), BFF relay endpoint for downloads, nav entries. Uses Contracts only. |
| **Tests** | `tests/TestSupport/Verification/*` (deterministic fakes and sample-document generators; **never referenced from `src/`**), unit/integration/bUnit/architecture tests per §13. |

### 2.2 Internal module boundaries (who may call whom)

```
Controllers ──► module services (IBankStatementVerificationService | IPanVerificationService | IPassportVerificationService)
                    │  share VerificationSubmissionCore (preflight, inspect, store, idempotency, insert request)
                    ▼
            IDocumentStorageService, IDocumentInspector, IVerificationMeter ──► ILicenseMeteringService (existing)
Worker ──► IVerificationProcessor ──► IModuleProcessor (one per VerificationType)
                    │                    uses IDocumentParser (digital text | OCR), IBankStatementParser / MRZ / PAN rules / name matcher,
                    │                    IPanVerificationProvider | IPassportVerificationProvider
                    ▼
            repositories + IVerificationMeter (charge per stage) + IAuditService + IWebhookPublisher  (one transaction per stage commit)
```
A module is **three things**: an `IVerificationModule` descriptor (catalogue entry), an `IDocumentVerificationService<TRequest>` (submission), and an `IModuleProcessor` (async pipeline). Licensing, auth, tenancy, audit, storage, worker, query, download and admin code never mention a concrete module; they work from `VerificationType` + descriptor data. Adding Aadhaar later = new enum member + descriptor flipped from Upcoming to Available + the three pieces + permission keys + `MeteredOperation` members. No edit to licensing, auth, tenant or audit code (ADR-17).

---

## 3. Verification-module catalogue (registry)

```csharp
namespace NexaVerify.Application.Verification.Modules;

public enum ModuleStatus { Available, Upcoming, Disabled }

public sealed record VerificationModuleDescriptor(
    string Key,                                   // stable URL/DB key: "bank-statement", "pan", "passport", "aadhaar", ...
    string DisplayName,
    string Description,
    ModuleStatus Status,                          // Available | Upcoming   (Disabled is computed, see below)
    int DisplayOrder,
    VerificationType? Type,                       // null for Upcoming
    IReadOnlyList<string> SubmitPermissions,      // empty for Upcoming
    IReadOnlyList<MeteredOperation> CostOperations, // empty for Upcoming  => cannot be charged
    IReadOnlyList<string> ProviderIds,            // authoritative providers this module can use ("pan", "passport"); empty = none
    string? PlannedNote);                         // free text shown for Upcoming ("Planned")

public interface IVerificationModule { VerificationModuleDescriptor Descriptor { get; } }

public interface IVerificationModuleRegistry
{
    IReadOnlyList<VerificationModuleDescriptor> All { get; }              // code-defined, ordered
    VerificationModuleDescriptor? Find(string key);
    VerificationModuleDescriptor? Find(VerificationType type);
    /// <summary>Effective status: Upcoming stays Upcoming; Available becomes Disabled when the platform switch (verification.ModuleSettings.IsEnabled) is off.</summary>
    Task<ModuleStatus> GetEffectiveStatusAsync(string key, CancellationToken ct);
}

public interface IModuleGate
{
    /// <summary>Failure = 404 VERIFICATION_MODULE_DISABLED (hidden like BILLING_DISABLED). Never succeeds for Upcoming/unknown.</summary>
    Task<Result<VerificationModuleDescriptor>> EnsureAvailableAsync(VerificationType type, CancellationToken ct);
}
```

* **Available** entries (R1): `bank-statement`, `pan`, `passport`, each registered as an `IVerificationModule` singleton in its own folder. Duplicated keys fail start-up (`IValidateOptions`-style registry validator).
* **Upcoming** entries are plain data in `Application/Verification/Modules/UpcomingModules.cs`: `aadhaar` "Aadhaar Verification", `driving-licence` "Driving Licence Verification", `voter-id` "Voter ID Verification", `digilocker` "DigiLocker Integration", `govt-documents` "Additional Government Document Verification", `kyc-fraud` "Additional KYC and fraud detection". `Type = null`, `SubmitPermissions = []`, `CostOperations = []`, `ProviderIds = []`.
* **Hard guarantees for Upcoming** (enforced by architecture/unit tests, §13): no controller route, no `IModuleProcessor`, no `MeteredOperation` member, no permission key, no row in `verification.ModuleSettings`, not accepted by `IModuleGate`. They are *catalogue only*; the UI renders them as non-clickable "Upcoming" cards (§11.1). The catalogue endpoint returns them with `status: "Upcoming"` and `available: false`.
* **Disabled** is an operator switch on an Available module (`PUT /admin/verifications/modules/{key}`); submissions return 404 `VERIFICATION_MODULE_DISABLED`; already accepted requests still complete and stay readable.
* **Per-tenant availability** is not part of R1 except via permissions (a client without `verification.pan.submit` never sees PAN). A per-plan/per-client entitlement can later be a `ClientSetting` read by `IModuleGate` without touching callers.
* The registry feeds: the client catalogue (`GET /api/v1/verifications/modules`), the admin module table, nav trimming (permission keys), the cost admin (operations per module), architecture tests.

---

## 4. Abstractions (exact names and signatures)

All in `NexaVerify.Application.Verification.*`, results use the existing `Result<T>`/`Error`. Every async method takes a `CancellationToken`.

### 4.1 Submission (one service per module — REQ §3)

```csharp
public interface IDocumentVerificationService<in TRequest>
{
    VerificationType Type { get; }
    Task<Result<VerificationAcceptedDto>> SubmitAsync(TRequest request, DocumentUpload? document, string? idempotencyKey, CancellationToken ct);
}
public interface IBankStatementVerificationService : IDocumentVerificationService<SubmitBankStatementRequest> { }
public interface IPanVerificationService          : IDocumentVerificationService<SubmitPanRequest> { }
public interface IPassportVerificationService     : IDocumentVerificationService<SubmitPassportRequest> { }

public sealed record DocumentUpload(string FileName, string? DeclaredContentType, byte[] Content);   // controller reads the form file into a bounded buffer
```
`VerificationSubmissionCore` (internal, shared) performs, in this order and **before anything is stored or charged**: current user must be a client principal → module gate → validate fields (FluentValidation validators `SubmitBankStatementRequestValidator` etc.) → idempotency replay lookup → queue-depth cap → `PreflightAsync` for the extraction operation (+ provider operation when a provider is configured for the type) → `IDocumentInspector.InspectAsync` (signature, extension, MIME, size, page count, encryption, active content) → store encrypted document → insert request (`Queued`) + `Uploaded=Completed` + event + audit in one transaction → `202`. No credit is deducted at this point (§8).

### 4.2 Processing

```csharp
public interface IVerificationProcessor
{   // called by the worker for ONE claimed request; runs in the request's tenant scope
    Task ProcessAsync(ClaimedRequest claim, CancellationToken ct);
}
public interface IModuleProcessor
{
    VerificationType Type { get; }
    Task<DocumentStageOutcome> ProcessDocumentAsync(ModuleContext ctx, CancellationToken ct);   // Extracted + RulesChecked + ConsistencyChecked
    Task<ExternalStageOutcome> VerifyExternalAsync(ModuleContext ctx, CancellationToken ct);    // never throws for expected conditions
}
public sealed record ModuleContext(VerificationRequestSnapshot Request, DocumentKind Kind, ReadOnlyMemory<byte> Document,
                                   IReadOnlyDictionary<string, string?> SubmittedFields, ExtractedData? Extracted, string CorrelationId);
public sealed record DocumentStageOutcome(StageState Extracted, StageState Rules, StageState Consistency, ExtractedData? Data,
                                          IReadOnlyList<CheckResult> Checks, IReadOnlyList<ReviewReason> Reasons, string? FailureCode);
public sealed record ExternalStageOutcome(ExternalState State, string? ProviderId, string? ProviderReference,
                                          IReadOnlyList<FieldMatch> FieldMatches, string? ReasonCode);
public sealed record CheckResult(string Code, CheckStage Stage, CheckOutcome Outcome, CheckSeverity Severity, string? Field, string Message /* PII-free template text */);
public sealed record FieldMatch(string Field, FieldMatchOutcome Outcome);
public sealed record ReviewReason(string Code, string Message);
```
`ExtractedData` is an abstract record with `[JsonDerivedType]` discriminators for `BankStatementData`, `PanData`, `PassportData` only (closed set, no polymorphic deserialisation of arbitrary types).

### 4.3 Document handling

```csharp
public interface IDocumentInspector          // cheap, bounded, run at accept time (5 s timeout, bulkhead semaphore)
{
    Task<Result<DocumentInspection>> InspectAsync(DocumentUpload upload, UploadPolicy policy, CancellationToken ct);
}
public sealed record DocumentInspection(DocumentKind Kind, string ContentType, string Extension, int PageCount,
                                        int? Width, int? Height, bool IsEncrypted, bool HasActiveContent, byte[] Sha256);
public enum DocumentKind { Pdf, Jpeg, Png, WebP }

public interface IDocumentParser             // bytes -> text lines; chooses digital text first, OCR when text is missing/poor
{
    Task<Result<DocumentText>> ParseAsync(ReadOnlyMemory<byte> content, DocumentKind kind, ParseOptions options, CancellationToken ct);
}
public sealed record DocumentText(IReadOnlyList<PageText> Pages, TextSource Source, decimal? MeanConfidence);   // Source: DigitalPdf | Ocr | Mixed
public sealed record PageText(int Number, IReadOnlyList<TextLine> Lines);
public sealed record TextLine(string Text, decimal? Confidence, int? X, int? Y, int? Width, int? Height);

public interface IPdfTextExtractor   { Task<IReadOnlyList<PageText>> ExtractAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken ct); }
public interface IPdfRasterizer      { Task<IReadOnlyList<byte[]>> RenderPagesAsync(ReadOnlyMemory<byte> pdf, int maxPages, int dpi, CancellationToken ct); } // PNG bytes
public interface IDocumentImagePreprocessor { Task<Result<byte[]>> PrepareForOcrAsync(ReadOnlyMemory<byte> image, CancellationToken ct); }           // Skia: decode caps, EXIF orient, grayscale, bounded size

public interface IOcrProvider
{
    string ProviderId { get; }               // "tesseract-cli", "none"
    bool IsAvailable { get; }                // binary + language data present (health, admin status)
    Task<OcrPage> RecognizeAsync(OcrRequest request, CancellationToken ct);   // throws OcrUnavailableException only for infrastructure faults
}
public sealed record OcrRequest(byte[] ImagePng, string Language, int PageNumber);
public sealed record OcrPage(int PageNumber, IReadOnlyList<TextLine> Lines, decimal? MeanConfidence);

public interface IDocumentStorageService
{
    Task<StoredDocument> StoreAsync(Guid clientId, Guid requestId, DocumentUpload upload, DocumentInspection inspection, CancellationToken ct); // encrypts with IClientEncryption, purpose "verification-document:{requestId:N}"
    Task<Result<byte[]>> ReadAsync(Guid documentId, CancellationToken ct);       // tenant filter applies; decrypts
    Task DeleteAsync(Guid documentId, CancellationToken ct);                     // hard delete content + metadata flag
}
```

### 4.4 Module-specific logic

```csharp
public interface IBankStatementParser
{
    string ParserId { get; }                                   // "generic-columnar"
    BankStatementParseResult Parse(DocumentText text, BankRuleSet rules);   // pure, deterministic, regex with timeouts
}
public interface IBankRuleSetProvider
{
    IReadOnlyList<BankRuleSet> RuleSets { get; }               // loaded from configured JSON files; "generic" always present
    BankRuleSet Detect(DocumentText text);                     // best match by detection patterns; falls back to "generic"
}
public sealed record BankRuleSet(string Id, string DisplayName, IReadOnlyList<string> DetectPatterns, BankHeaderPatterns Header,
                                 BankTransactionPatterns Transactions, IReadOnlyList<string> DateFormats, AmountFormat Amounts, string DefaultCurrency);

public interface IPanVerificationProvider
{
    string ProviderId { get; }
    bool IsConfigured { get; }
    Task<ProviderResult> VerifyAsync(PanProviderRequest request, CancellationToken ct);
}
public sealed record PanProviderRequest(string Pan, string? FullName, DateOnly? DateOfBirth, string CorrelationId);

public interface IPassportVerificationProvider
{
    string ProviderId { get; }
    bool IsConfigured { get; }
    Task<ProviderResult> VerifyAsync(PassportProviderRequest request, CancellationToken ct);
}
public sealed record PassportProviderRequest(string PassportNumber, string IssuingCountry, string? FullName, DateOnly? DateOfBirth, DateOnly? DateOfExpiry, string CorrelationId);

public sealed record ProviderResult(ExternalState State, string? ProviderReference, IReadOnlyList<FieldMatch> FieldMatches, string? ReasonCode);

// Pure helpers (Application/Verification/Passport, Pan, Matching)
public static class MrzTd3Parser { public static MrzParseResult Parse(IReadOnlyList<string> candidateLines, DateOnly today); }
public static class MrzCheckDigit { public static int Compute(ReadOnlySpan<char> field); public static bool IsValid(ReadOnlySpan<char> field, char digit); }
public static class PanRules      { public static PanValidation Validate(string? pan, PanRuleOptions options); }
public interface INameMatcher     { NameMatch Compare(string? submitted, string? extracted); }   // returns FieldMatchOutcome + score (score never leaves the server)
public interface IVerificationMeter // thin billing facade, the only code that talks to ILicenseMeteringService for M14
{
    Task<Result<MeterTicket>> PreflightAsync(Guid clientId, VerificationType type, bool includeProvider, CancellationToken ct);
    Task<Result<StageCharge>> ChargeStageAsync(Guid clientId, Guid requestId, VerificationStage stage, MeterOutcome outcome, CancellationToken ct); // idempotency key vr:{requestId:N}:x|p
}
```

### 4.5 Provider replaceability, configuration, secrets

* A provider is selected by id from configuration (`Verification:Providers:Pan:Provider` = `None` | `<vendorId>`), resolved through `IPanVerificationProviderResolver` (a keyed registration; the DI registration code is the only place that knows vendor ids). Swapping vendors = new class + config value; no change to services, DTOs, storage, ledger.
* **R1 ships only `NotConfigured*` providers** (`IsConfigured=false`, `VerifyAsync` returns `ExternalState.ProviderNotConfigured`). No vendor is named in the requirement and no contract/credentials exist, so a real adapter cannot be written or verified; it is a follow-up task once the product owner names the vendor (§15 Q1–Q3). We deliberately do **not** ship a speculative "generic HTTP" adapter.
* When a vendor adapter is added it must: be a typed `HttpClient` (`AddHttpClient<TAdapter>`), base address from config, **https only**, host fixed by config (no URL ever derived from request data → SSRF), `AllowAutoRedirect=false`, TLS validation on, per-call timeout `Verification:Providers:<Type>:TimeoutSeconds` (default 10), credentials only from configuration/secret files (`Verification:Providers:<Type>:<Vendor>:ApiKey` etc., never logged; the options type uses `[Sensitive]`), response size cap (1 MB), map every exception to `Timeout`/`ProviderError` (never throw expected failures), never log request/response bodies, store only `State`, `ProviderReference`, `FieldMatches`, timestamps. A hand-rolled `ProviderCircuitBreaker` (open after `Verification:Providers:BreakerFailures`=5 consecutive failures for `BreakerOpenSeconds`=60, then one trial) prevents hammering a down vendor; while open the call returns `ProviderError` with reason `CIRCUIT_OPEN` without consuming an attempt. (ADR-26: no Polly dependency; retry policy lives in the job, which is idempotent and observable.)
* **No authoritative provider is defined for bank statements in R1.** Their `ExternalVerification` is always `NotRequested`. No `IBankStatementVerificationProvider` is created (YAGNI); adding one later is additive.
* Secrets: configuration/secret-file only (`deploy/CONFIG.md` pattern: `/run/secrets`, env). The admin API shows `configured: true|false`, provider id, last-24 h outcome counts; never a key, URL credential or key length.

### 4.6 Fake providers for tests (REQ §14)

`tests/TestSupport/Verification/`: `FakePanVerificationProvider`, `FakePassportVerificationProvider` (scripted by PAN/passport number: e.g. numbers ending in `…V`→Verified, `…N`→NotVerified, `…I`→Inconclusive, `…E`→ProviderError, `…T`→Timeout; or an explicit queue of results), `FakeOcrProvider` (returns canned `OcrPage` keyed by image SHA), `SampleDocuments` (generators that build synthetic digital PDFs/PNGs with known content — never real customer documents). They are registered only through `ApiFactory.WithWebHostBuilder(... ConfigureTestServices ...)`. `src/**` must not reference `TestSupport`, and production DI must not contain a type whose name starts with `Fake` (architecture test). A `Development`-only simulated provider (like `SimulatedProvider` for payments) is **not** created: it would be a path to fabricated "Verified" results.

---

## 5. Configuration (typed, validated at start-up with `ValidateOnStart`)

All limits below are configuration, none are literals in code. Document in `deploy/CONFIG.md` (DevOps).

| Key | Default | Notes |
|---|---|---|
| `Verification:Upload:MaxFileBytes` | 10485760 (10 MB) | Per file. Also sets the per-route `[RequestSizeLimit]` = MaxFileBytes + 1 MB (overrides the 6 MB global `RequestLimits:MaxRequestBodyBytes` for these routes only). |
| `Verification:Upload:AllowedExtensions` | `.pdf,.jpg,.jpeg,.png,.webp` | Extension is only one of four independent checks. |
| `Verification:Upload:MaxPages:BankStatement` / `:Pan` / `:Passport` | 30 / 2 / 4 | PDF pages. Images count as 1. |
| `Verification:Upload:MaxImagePixels` | 40000000 | Decode guard (same idea as the face processor). |
| `Verification:Upload:RejectActiveContent` | true | PDFs containing JavaScript, `/Launch`, `/EmbeddedFile`, `/OpenAction` with action, XFA are rejected. |
| `Verification:Upload:InspectTimeoutSeconds` / `:InspectMaxConcurrent` | 5 / 4 | Bulkhead for the inspector. |
| `Verification:Limits:MaxQueuedPerClient` | 50 | Queued+Processing requests per client; over -> 429 `VERIFICATION_QUEUE_FULL`. |
| `Verification:Worker:Enabled` / `:Parallelism` / `:PollSeconds` / `:BatchSize` | true / 2 / 2 / 5 | Set `Enabled=false` on web-only nodes later. |
| `Verification:Worker:LeaseSeconds` | 300 | Validator: must exceed `Processing:TimeoutSeconds` + 30 (like the webhook lease rule). |
| `Verification:Processing:TimeoutSeconds` / `:MaxAttempts` / `:BackoffSeconds` | 120 / 3 / 30,120,600 | Document stage budget; attempts counted at claim. |
| `Verification:Processing:MaxQueueAgeMinutes` | 60 | Older `Queued` requests become `Expired` (no charge). |
| `Verification:Ocr:Provider` | `tesseract-cli` | `none` disables OCR (scanned documents then fail with `OCR_UNAVAILABLE`). |
| `Verification:Ocr:Tesseract:ExecutablePath` / `:TessdataPath` / `:Languages` / `:TimeoutSecondsPerPage` / `:MaxConcurrent` | `tesseract` / image default / `eng` / 30 / 2 | CLI invoked with an argument list (no shell). |
| `Verification:Ocr:Dpi` / `:MinTextCharsPerPage` | 300 / 40 | Below the character threshold a PDF page is treated as scanned and rasterised. |
| `Verification:Ocr:MinMeanConfidence` | 0.60 | Below -> review signal, not failure. |
| `Verification:BankStatement:RuleSetsPath` | `config/bank-rules` | Directory of `*.json` rule sets; invalid files fail start-up. |
| `Verification:BankStatement:BalanceTolerance` / `:MaxTransactions` / `:MaxPeriodDays` / `:DefaultCurrency` | 0.01 / 5000 / 400 / `INR` | |
| `Verification:Pan:Pattern` | `^[A-Z]{5}[0-9]{4}[A-Z]$` | Configurable (jurisdiction). |
| `Verification:Pan:HolderTypes` | `A,B,C,F,G,H,L,J,P,T` | 4th character table, §6.5. Confirm against current Income Tax Department documentation before go-live. |
| `Verification:Passport:NumberPatterns:<ISO3>` / `:Default` | `IND: ^[A-Z][0-9]{7}$`, default `^[A-Z0-9]{6,9}$` | |
| `Verification:Names:MatchThreshold` / `:PartialThreshold` / `:IgnoredTokens` | 0.90 / 0.75 / `MR,MRS,MS,DR,SHRI,SMT,KUMARI,MISS` | |
| `Verification:Providers:Pan:Provider` / `Passport:Provider` | `None` | `None` => `ProviderNotConfigured`. |
| `Verification:Providers:<Type>:TimeoutSeconds` / `:MaxAttempts` / `:BreakerFailures` / `:BreakerOpenSeconds` | 10 / 3 / 5 / 60 | |
| `Verification:Providers:StoreRawResponse` | false | Keep false; raw provider payloads contain PII. |
| `Verification:RequireConsentReference` | true | `consentReference` required on submit (DPDP/GDPR duty sits with the client; platform records the reference). |
| `Verification:Retention:DocumentDays` / `:ResultDays` / `:MetadataDays` / `:PersonalMetadataDays` | 7 / 90 / 730 / 90 | Hard caps: document <= 90, result <= 365. §9.4. |
| `Verification:Result:InlineTransactions` / `:IncludeExtractedPersonalFields` | 500 / true | |
| `Verification:Download:LinkSeconds` / `:AllowOriginalDownload` | 60 / false | Original-document download is off by default (§10.6). |
| `Throttle:VerificationSubmitsPerMinute` / `Throttle:VerificationDownloadsPerMinute` | 20 / 30 | Per user/API key, shared counters. |

---

## 6. OCR and parsing strategy (realistic, permissive licences)

### 6.1 Libraries (versions must be verified at implementation time; nothing below was restored or built in this design step)

| Capability | Choice | Licence | Notes / what to verify |
|---|---|---|---|
| Digital PDF text, page count, encryption/active-content detection | **PdfPig** (NuGet id `PdfPig`; a web search showed 0.1.x releases, 0.1.15 appears current) | Apache-2.0 (verify on the package page) | Pure managed, no native deps. Pre-1.0: public API may change between minors — pin exactly, wrap behind `IPdfTextExtractor`/`IDocumentInspector`. It has no hard memory/decompression budget; bound by size, page cap, per-call timeout and bulkhead (§12). Active-content scan needs raw object access: verify PdfPig exposes catalog/`/OpenAction`/`/JS`/`/EmbeddedFiles` or fall back to a conservative byte-token scan (`/JavaScript`, `/JS`, `/Launch`, `/EmbeddedFile`, `/XFA`, `/OpenAction`) *plus* object-stream awareness. |
| PDF page rasterisation (scanned PDFs) | **PDFtoImage** (PDFium + SkiaSharp wrapper) behind `IPdfRasterizer` | MIT wrapper; PDFium is BSD-3/Apache-2.0 (verify) | Cross-platform natives for linux-x64/win-x64. Depends on SkiaSharp: the compatible SkiaSharp version range **must be verified against the pinned `3.119.2`** (search showed 4.x/5.0-preview lines; an older PDFtoImage may be needed). If incompatible, fallback in order: (a) bump SkiaSharp centrally (DevOps + regression of face tests), (b) `pdftoppm` (poppler) as an out-of-process CLI in the Docker image — poppler is GPL; invoking an unmodified binary is not linking but needs legal sign-off, (c) accept image uploads only for scanned documents (PDF scans fail with `OCR_UNAVAILABLE`). **Not** MuPDF (AGPL). |
| OCR engine | **Tesseract** (Apache-2.0) invoked as an **out-of-process CLI** via `TesseractCliOcrProvider` | Apache-2.0 | Chosen over the `Tesseract` NuGet wrapper because the wrapper's Linux native packaging (leptonica/tesseract library names) is a known source of breakage and an in-process native crash would take down the API; the CLI isolates crashes/hangs/memory, can be killed on timeout, and is installable with `apt-get install tesseract-ocr tesseract-ocr-eng` (add `tesseract-ocr-hin` if Devanagari is needed). Arguments are passed as an argument list with `stdin`/`stdout` (`tesseract stdin stdout -l eng --psm 6 tsv`), never through a shell; one process per page with a kill timeout; `TSV` output provides per-word confidence. Docker implication: runtime image grows (~40–60 MB, verify), read-only root FS needs a writable `/tmp` for tesseract (`tmpfs`), non-root user already used. |
| Image decode / grayscale / resize | **SkiaSharp** (already present) | MIT | `SkiaDocumentImagePreprocessor`. |
| Spreadsheet-style or HTML statements | Out of scope R1 | – | Only PDF/JPEG/PNG/WebP. |

#### 6.1 as built (Backend-B version spike, executed in the sandbox: restore, compile, run on generated documents)

| Capability | Decision / final pin | Evidence and findings |
|---|---|---|
| Digital PDF text, page count, encryption and object-graph scan | **PdfPig `0.1.16`** (Apache-2.0; stable line, newest non-alpha) | Restored and run. `PdfDocument.Open`, `GetPage().GetWords()`, `IsEncrypted`, `NumberOfPages` and the token API (`Structure.CrossReferenceTable.ObjectOffsets`, `Structure.GetObject`) are used. No vulnerability reported by `dotnet list package --vulnerable --include-transitive`. |
| PDF rasterisation | **PDFtoImage `5.2.1`** (MIT; PDFium `bblanchon.PDFium.*` `147.0.7690`, BSD-3/Apache-2.0) | `5.2.1` is the newest line whose dependency is exactly the pinned SkiaSharp `3.119.2`; `5.3.0` and later require SkiaSharp 4.x, `5.1.x` needs 3.119.0, so SkiaSharp stays at `3.119.2` and the face pipeline is untouched. Rendering of A4 pages at 100 dpi and the pixel cap were verified on linux-x64. The package also pulls the win32/macOS native assets (about 15 MB of unused files in the publish output on Linux; a RID-specific publish trims them; DevOps to confirm). Every PDFtoImage call is annotated platform-specific (CA1416): `PdfiumRasterizer` is `[SupportedOSPlatform]` for linux/windows/macos and `UnavailablePdfRasterizer` is registered elsewhere (scanned PDFs then fail with `OCR_UNAVAILABLE`). poppler (GPL) and MuPDF (AGPL) were not used. |
| OCR | **Tesseract 5.x as a command-line process** behind `IOcrProvider` (`TesseractCliOcrProvider`), Apache-2.0 | The Tesseract NuGet wrapper was not evaluated (the CLI isolates crashes and hangs, see above). `apt-get install tesseract-ocr tesseract-ocr-eng` (5.3.4) worked in the sandbox and an end-to-end test (rendered PAN card PNG -> preprocessor -> Tesseract -> PAN processor) passes; the same tests are skipped with an explicit reason on a machine without the binary. Image on stdin, TSV on stdout, no shell, argument list, validated language token, private empty working directory deleted afterwards, output capped, process tree killed on timeout. |
| Image decode/grayscale | SkiaSharp `3.119.2` (unchanged) | PNG with an alpha channel needs `SKAlphaType.Premul` when decoding (an `Opaque` request returned null; found by the end-to-end test). |

Configuration added by this step (typed, validated at start-up): `Verification:DocumentPipeline:*` (`DocumentPipelineOptions`: OCR max image side, PDF scan ceilings, trailing-blank-bytes tolerance, OCR output cap, probe cache and timeout) and `Verification:Passport:MaxAgeYears`. Structural scan ceilings are options, not literals.

Bank rule sets: `generic` plus three SYNTHETIC test layouts (`synthetic-layout-a|b|c`) are embedded as JSON data in `Application/Verification/BankStatements/RuleSets`; additional `*.json` files in `Verification:BankStatement:RuleSetsPath` are loaded and validated at start-up (a bad file stops the host). **No real bank layout ships: a rule set for a real bank requires sample statements (open question Q6) and says nothing here about any bank's format.** The generic rule set reads "date, narration, amount, balance" lines and infers the direction of an unmarked amount from the change in the running balance.

**Not verifiable without external access / in this sandbox:** actual OCR accuracy on real Indian bank statements, PAN cards and passports (no real documents may be committed; synthetic documents prove plumbing only); the PDFtoImage/SkiaSharp compatibility; Tesseract availability inside the sandbox and the production image; any vendor provider behaviour. Accuracy targets therefore cannot be asserted; QA must run an accuracy pass with product-owner-supplied, consented, anonymised samples (open question Q6) before go-live.

### 6.2 Text acquisition pipeline (`DocumentParser`)
1. PDF: `PdfPigTextExtractor` per page. If a page yields fewer than `Ocr:MinTextCharsPerPage` characters it is *scanned*: rasterise that page (`IPdfRasterizer`, `Ocr:Dpi`) -> preprocess -> OCR. Pages with enough text keep digital text (`Source = Mixed` when both occur).
2. Images: preprocess -> OCR.
3. OCR unavailable (`IsAvailable=false`) and a scanned page found -> stage fails with `OCR_UNAVAILABLE` (not billed, request `Failed`); never silently "succeeds" with empty text.
4. Output is only text lines + confidences (no pixels leave the process; raster pages are in memory, zeroed after use).

### 6.3 Bank statement parsing (`IBankStatementParser`, generic + configurable rule sets)
* `IBankRuleSetProvider.Detect` scores each rule set's `DetectPatterns` over the first two pages; best score wins, else `generic`.
* A rule set is **data** (JSON, version-controlled, one file per bank layout): detection patterns, header patterns (holder, account number, period "from/to", opening/closing balance, bank name), transaction-line pattern(s) (a regex with named groups `date`, `valueDate`, `desc`, `ref`, `debit`, `credit`, `balance`, optionally a column-layout form using X positions from OCR/PdfPig), accepted date formats (`dd/MM/yyyy`, `dd-MMM-yyyy`, …), amount format (decimal and thousands separators, `Dr/Cr` suffix, lakh grouping `1,00,000.00`), default currency. Multi-line descriptions are joined to the preceding transaction when a line has no leading date.
* All regexes run with `RegexOptions.CultureInvariant` and a **match timeout of 250 ms** (ReDoS); rule files are validated at start-up (compile, timeout, named groups); a bad file fails start-up, not a request.
* No bank-specific code in controllers or services. Shipped rule sets in R1: `generic` plus synthetic test layouts only. **Real bank layouts need sample statements** (Q6); until a bank has a rule set it is handled by `generic` with lower expected quality, which surfaces as `BS_EXTRACTION_CONFIDENCE`/`BS_COMPLETENESS` review signals rather than errors.

### 6.4 Bank statement normalised model and checks
```
BankStatementData { SchemaVersion=1, RuleSetId, BankName?, AccountHolderName?, AccountNumberMasked? ("XXXXXX1234"),
                    Currency, PeriodFrom?, PeriodTo?, OpeningBalance?, ClosingBalance?, PageCount, TextSource, MeanConfidence?,
                    Transactions[] }
BankTransaction   { Index (1..n in document order), Date, ValueDate?, Description (<=200), Reference?, Debit?, Credit?, Balance?, Confidence? }
```
Money is `decimal`, rounded to 2 dp at the parser boundary; comparisons use `BalanceTolerance`. The full account number is **not** stored: masked value + `AccountNumberHash` (HMAC) only. Check catalogue (stable `Code`s; `Severity`: `Info|Warning|Review`; a failed check is a *review signal*, never a fraud finding — messages say "inconsistent", never "forged/tampered/fake"):

| Code | Stage | Rule |
|---|---|---|
| `BS_COMPLETENESS` | Rules | Holder, bank, account, period, opening and closing balance present (each missing item listed by name, no values). |
| `BS_PERIOD_VALID` | Rules | `PeriodFrom <= PeriodTo`, `PeriodTo <= today`, span `<= MaxPeriodDays`. |
| `BS_ROW_SHAPE` | Rules | Each row has a valid date and exactly one of debit/credit (both or neither -> Fail). |
| `BS_TX_IN_PERIOD` | Consistency | Every transaction date within the period (count of outliers). |
| `BS_TX_ORDER` | Consistency | Dates monotonic (ascending or descending for the whole statement); mixed -> Warning with the index of the first break. |
| `BS_ROW_ARITHMETIC` | Consistency | Where both balances are known: `prev + credit - debit == balance` (±tolerance) for each row; reports count and first failing index. Direction-aware. |
| `BS_OPENING_CONTINUITY` | Consistency | `opening` +/- first row equals first running balance. |
| `BS_CLOSING_CONTINUITY` | Consistency | Last running balance equals `closing`. |
| `BS_TOTALS` | Consistency | `opening + Σcredits - Σdebits == closing`. |
| `BS_DUPLICATES` | Consistency | Identical (date, description, amount, balance) rows -> Warning; identical without balance -> Info (legitimate repeats exist). |
| `BS_SUBMITTED_MATCH` | Consistency | Submitted holder name (via `INameMatcher`), bank name, account last 4, period vs extracted -> `FieldMatch` entries. |
| `BS_EXTRACTION_CONFIDENCE` | Rules | Mean OCR confidence `< Ocr:MinMeanConfidence` or many rows without parseable amounts -> Review. |
| `BS_EXTERNAL_NOT_PERFORMED` | External | Info: "No authoritative statement verification is performed for bank statements." |

Checks that cannot run because data is missing are recorded as `NotEvaluated` (never silently skipped, never "Pass").

### 6.5 PAN rules (`PanRules`)
* Normalise: trim, upper-case, remove spaces/hyphens. Format = configured regex (default 5 letters + 4 digits + 1 letter).
* 4th character = holder type from the configurable table: `A` Association of Persons, `B` Body of Individuals, `C` Company, `F` Firm/LLP, `G` Government, `H` Hindu Undivided Family, `J` Artificial Juridical Person, `L` Local Authority, `P` Individual (Person), `T` Trust. Unknown letter -> `PAN_HOLDER_TYPE_UNKNOWN` (Fail on rules).
* 5th character: for type `P` it is expected to be the first letter of the holder's surname; with a submitted/extracted name this is a **Warning** consistency check (name order varies), never a fail. For non-individual types it is the first letter of the entity name.
* The last character is an alphabetic check character whose algorithm is **not publicly specified**; the platform does not claim to validate it. Consequence stated in the UI/report: *a well-formed PAN is not evidence that the PAN exists, is active or belongs to the person* (REQ §5).
* Checks: `PAN_FORMAT`, `PAN_HOLDER_TYPE`, `PAN_NAME_INITIAL` (Warning), `PAN_DOC_NUMBER_MATCH` (document PAN vs submitted PAN), `PAN_NAME_MATCH`, `PAN_DOB_MATCH`, `PAN_FATHER_NAME_MATCH` (field matches only report `Match|PartialMatch|Mismatch|NotProvided|NotExtracted`), `PAN_EXTRACTION_CONFIDENCE`.
* OCR confusion repair in the number field only when the result then satisfies the pattern (`O<->0`, `I/L<->1`, `S<->5`, `B<->8`, `Z<->2` by position class) and then a Warning `PAN_OCR_CORRECTED` is raised.
* A request may be **number-only** (no document): stages `Extracted` = `Skipped`, rules run on the submitted number, provider stage may run.

### 6.6 Passport and MRZ (TD3)
* TD3 = two lines of 44 characters. Line 1: `P`, type, issuing state (3), `surname<<given<names`. Line 2: positions 0–8 document number, 9 check, 10–12 nationality, 13–18 date of birth `YYMMDD`, 19 check, 20 sex (`M|F|<`), 21–26 expiry `YYMMDD`, 27 check, 28–41 personal number, 42 check (may be `<`/`0` when empty), 43 **composite** check over positions `0–9`, `13–19`, `21–42` (39 characters).
* Check digit (ICAO 9303): characters mapped `0–9 -> 0–9`, `A–Z -> 10–35`, `< -> 0`; multiplied by weights `7,3,1` repeating; sum mod 10.
* Finding the MRZ: take the last two non-empty lines of the page text (or any two consecutive lines) after stripping spaces and mapping look-alike characters (`«`, `‹` -> `<`); accept when both are 44 chars after repair and line 1 starts with `P`. TD1/TD2 (3x30, 2x36) -> `MRZ_FORMAT_UNSUPPORTED` Info (R1 supports TD3 only; Indian passports are TD3).
* OCR repair: in numeric fields (dates, check digits) map `O->0, I->1, B->8, S->5, Z->2`; in alphabetic fields map the reverse; any repair that makes a check digit pass raises Warning `MRZ_OCR_CORRECTED`. Never repair silently.
* Century: expiry -> `2000+YY`; date of birth -> `19YY` when `20YY` would be in the future, else `20YY` (configurable pivot). Dates are validated as real calendar dates.
* Visual inspection zone (VIZ): label patterns extracted from OCR text (`Passport No`, `Surname`, `Given Name(s)`, `Nationality`, `Date of Birth`, `Date of Issue`, `Date of Expiry`, `Sex`, `Place of Birth`). Issue date exists only in the VIZ.
* Checks: `PP_MRZ_PRESENT`, `PP_MRZ_CHECK_DOC`, `PP_MRZ_CHECK_DOB`, `PP_MRZ_CHECK_EXP`, `PP_MRZ_CHECK_PERSONAL`, `PP_MRZ_CHECK_COMPOSITE` (each `Pass|Fail|NotEvaluated`), `PP_NUMBER_FORMAT` (per-country pattern), `PP_DATES_VALID`, `PP_NOT_EXPIRED` (expiry `>=` today; expired -> Fail/Review, document stays readable), `PP_ISSUE_BEFORE_EXPIRY`, `PP_VALIDITY_SPAN` (Warning when > configured years), `PP_AGE_PLAUSIBLE`, `PP_VIZ_MRZ_CONSISTENT` (number, DOB, expiry, nationality, surname/given names), field matches against submitted data, `PP_EXTRACTION_CONFIDENCE`.
* MRZ validity proves internal consistency only; **never** reported as "genuine".

### 6.7 Name normalisation and matching (`INameMatcher`)
Normalise: Unicode `FormKD` -> strip combining marks -> upper-case invariant -> replace non-letters with space -> drop configured honorifics/titles -> collapse whitespace -> tokens. Compare as **order-insensitive token sets** with per-token similarity (normalised Damerau–Levenshtein; initials match a token with the same first letter). Outcome: `Match` (all tokens of the shorter name matched at `>= MatchThreshold`, ≤ 1 unmatched middle token), `PartialMatch` (`>= PartialThreshold` or one name is a token subset), `Mismatch`, `NotProvided`, `NotExtracted`. Scores stay server-side; clients get the outcome only. Deterministic, culture-invariant, covered by table-driven unit tests (diacritics, reordered, initials, honorifics, transliteration variants, empty).

---

## 7. Outcome model, state machine and processing model

### 7.1 Separated outcomes (REQ §3) — stored as independent columns, never collapsed

| Flag | Values | Meaning |
|---|---|---|
| `Uploaded` | `NotStarted`/`Completed`/`Failed` | File accepted, inspected, stored. |
| `Extracted` | `NotStarted`/`Completed`/`Failed`/`Skipped` | Text/fields could be read. `Skipped` for number-only PAN. |
| `RulesChecked` | same | Format and business-rule checks **ran** (not "all passed"). |
| `ConsistencyChecked` | same | Cross-field / cross-source checks **ran**. |
| `ExternalVerification` | `NotRequested` · `ProviderNotConfigured` · `Pending` · `Verified` · `NotVerified` · `Inconclusive` · `ProviderError` · `Timeout` | Authoritative result only. |
| `ReviewRequired` | bool + `ReviewReasons[]` (code + PII-free message) | Any Fail/Review-severity check, low confidence, provider `NotVerified`/`Inconclusive`/`ProviderError`/`Timeout`. |

`Completed` on a stage means *the stage ran*. Whether it passed is told by the individual `Checks` (`Pass|Warn|Fail|NotApplicable|NotEvaluated`), summarised as counts.

**Assurance (server-computed, drives all wording):** `AssuranceLevel` = `None` (nothing extracted) → `ExtractedOnly` → `ChecksCompleted` (rules + consistency ran) → `AuthoritativelyVerified`. **Only `ExternalVerification = Verified` yields `AuthoritativelyVerified`.** Therefore:

> Rule V-1: nothing is ever called *authentic*, *genuine*, *valid*, *active* or *verified* unless `ExternalVerification = Verified` with a non-empty `ProviderId` and `ProviderReference` recorded. OCR success, format validity, MRZ check digits and consistency checks only ever produce `ExtractedOnly`/`ChecksCompleted`.
>
> Enforced in code: `VerificationRequest.RecordExternalResult(ExternalState state, string? providerId, string? providerReference, …)` throws `DomainException("VERIFIED_REQUIRES_PROVIDER")` when `state == Verified` and either id is empty; `AssuranceLevel` is computed, not settable; DTOs contain no boolean named `isAuthentic/isGenuine/isValid/isVerified` (reflection test over `Contracts.Verification`); UI strings come from one table (§11.4).

### 7.2 Provider-stage rules (safe defaults, REQ §3/§5/§6)
| Situation | Result |
|---|---|
| Module has no provider (bank statements) | `NotRequested` |
| Provider id `None` / `IsConfigured=false` | `ProviderNotConfigured` (Info check `EXTERNAL_NOT_PERFORMED`; overall status unaffected; `ReviewRequired` false for this reason alone) |
| Call queued/in flight | `Pending` |
| Provider says the data matches | `Verified` (with reference) |
| Provider says no match / not found | `NotVerified` (definitive negative -> `ReviewRequired`, reason `PROVIDER_NOT_VERIFIED`) |
| Provider answers ambiguously | `Inconclusive` (`ReviewRequired`) |
| HTTP/network/5xx/circuit open after `MaxAttempts` | `ProviderError` (`ReviewRequired`, reason `PROVIDER_UNAVAILABLE`); can be retried later (§10.2) |
| Timeout after `MaxAttempts` | `Timeout` (same handling) |
| Provider stage unaffordable at charge time | stays `NotRequested`, reason `PROVIDER_STAGE_UNFUNDED`, result discarded (§8.4) |

### 7.3 Overall status and transitions
`VerificationStatus`: `Queued`, `Processing`, `Completed`, `ReviewRequired`, `Failed`, `Cancelled`, `Expired`.

```
            ┌─────────── cancel (client) ───────────┐
Queued ──claim──► Processing ──ok, no review──► Completed        (terminal)
  │  ▲               │  │ └──ok, ReviewRequired─► ReviewRequired  (terminal in R1)
  │  └─lease expired/│  └──unreadable, limits, credits,
  │    retryable err │      max attempts reached──► Failed          (terminal)
  │    (attempts<max)│  └──cancel observed between stages──► Cancelled (terminal)
  ├─ cancel ──► Cancelled
  └─ older than MaxQueueAgeMinutes ──► Expired (terminal, never charged)
Completed/ReviewRequired ──retry-external (stage 5 only)──► Processing ──► Completed/ReviewRequired
```
Allowed transitions are the only ones in `VerificationRequest.Transition(to)`; any other throws `DomainException("VERIFICATION_INVALID_TRANSITION")` (unit-tested exhaustively like `License`). Every transition appends a `verification.Events` row (`From`, `To`, `Stage`, `NoteCode`, `ActorType`, `At`) in the same transaction. `Completed` means the pipeline finished and nothing needs attention; it does **not** mean verified. `ReviewRequired` is terminal in R1 (no resolution workflow; a future `ReviewResolved` flag is additive). A request in `Processing` whose lease expired returns to `Queued` (`Attempts` already counted); at `Attempts >= MaxAttempts` it becomes `Failed` with `PROCESSING_FAILED`.

### 7.4 Sync vs async — decision: **async** (ADR-18)
* **Decision:** `POST` stores the document and enqueues; it answers `202 Accepted` with `requestId`, `Location: /api/v1/verifications/{id}/status`, and `Retry-After` hint. A background `VerificationWorker` processes; clients poll `status`/`result` or subscribe to the webhook `verification.completed`; the portal polls.
* **Why:** OCR of 30 pages and provider calls take seconds to minutes (the API has a 60 s request ceiling and a slow-loris floor), OCR is CPU-bound and must be bulkheaded away from request threads, provider outages need retries without holding the caller, and crash-safety (lease + retry) is required for billable work. Sync would couple the HTTP timeout to the slowest vendor.
* **Trade-offs:** a polling client; more states; storage of the document for the processing window (mitigated by retention §9.4); eventual result. Accept-path stays cheap and bounded so abuse cannot consume worker time without paying: preflight credits, per-principal throttle, queue cap.
* **Worker semantics** (patterned on `EmailDispatcher`/`EmailStore`): `VerificationStore.ClaimDueAsync` = one `UPDATE … OUTPUT … WITH (UPDLOCK, READPAST, ROWLOCK)` setting `Status=Processing, LeaseToken, LeaseUntil, Attempts+1` in platform scope; each request is then processed inside `ITenantScope.BeginTenant(clientId)` in a fresh DI scope; at-least-once with idempotent stages (a stage that already shows `Completed` is skipped on re-claim; charge keys make re-charge impossible). Linked cancellation token = host stopping token + `Processing:TimeoutSeconds` + the request's cancel flag polled between stages. Graceful shutdown hands claimed requests back (`Status=Queued`, attempt not counted) as `EmailDispatcher` does. Backoff `NextAttemptAt = now + BackoffSeconds[attempt-1] + jitter`.
* **Idempotency:** submit key derivation as faces (`SHA256(client|type|callerKey|documentSha|canonical fields)`), unique filtered index `(ClientId, IdempotencyKey)`; replay of an accepted key returns `200` with the original `VerificationAcceptedDto` (`replayed:true`) and never stores a second document or schedules a second job. A race on the unique index rolls back the loser (including its stored blob row) and returns the winner.
* **Cancellation:** `POST …/cancel`: `Queued` -> `Cancelled` immediately; `Processing` sets `CancelRequestedAt`, the worker observes it between stages and ends `Cancelled`; completed requests -> 409. Credits already charged for a completed stage stay charged (the stage was delivered); no charge happens after cancellation.

---

## 8. Credit policy (decisions)

Strictly through `ILicenseMeteringService` (preflight, `ChargeAsync`) and the existing ledger. No balance column, no parallel counter, no reservation table.

### 8.1 Decision summary (D = decision, with rationale)
| # | Decision | Rationale |
|---|---|---|
| C-1 | **Charge on completion of each billable stage**, in the same database transaction that persists that stage's result and status transition. No up-front consume, no "hold". | Same model as faces (charge only for a definitive answer; M4-M2: withhold the result if the charge fails). Atomic with the state change, so a crash can neither charge without a result nor deliver a result without a charge. Needs **no refund path** for system failures because nothing was charged before success. |
| C-2 | **Two billable stages per type:** *Extraction* (upload + extract + rules + consistency; `…Extraction` operation) and *Provider* (authoritative call; `…ProviderCheck`). Bank statements have only the extraction stage. | REQ §8 "per processing stage". Separates cheap local work from paid vendor calls. |
| C-3 | **Preflight at accept** requires credits for extraction **plus** provider cost when a provider is configured for the type (so a request cannot be accepted that could never pay for its main path) → `402` with the existing license codes. Preflight again in the worker before any heavy work → `Failed` / `CREDITS_UNAVAILABLE` (no work done, nothing charged). | Cheap early reject; bounds wasted compute from a race (`MaxQueuedPerClient`). |
| C-4 | **Outcome → `MeterOutcome` mapping.** Extraction stage: completed pipeline (including `ReviewRequired`) → `Success`; unreadable/limit/OCR-unavailable/processing failure/cancel/expiry → `Failed`. Provider stage: `Verified` → `Success`, `NotVerified` → `NoMatch`, `Inconclusive`/`ProviderError`/`Timeout`/`ProviderNotConfigured` → `Failed`. With the default policy `OnCompleted` (charges `Success` and `NoMatch`, not `Failed`) this yields the rules in 8.3. | Reuses `ChargePolicy.ShouldCharge` unchanged. An admin can set `OnSuccess` per rule (e.g. do not charge `NotVerified`). |
| C-5 | **Failed processing never consumes credits** (system faults and unreadable documents). **Provider failures never consume credits** (`ProviderError`, `Timeout`, `Inconclusive`, not configured). **Manual-review outcomes do consume** the stage credits, because the stage was delivered and the review signal is the product; a `NotVerified` provider answer consumes the provider stage (it is a definitive authoritative answer). | Fair, simple, predictable for customers; matches D2 in `docs/05` ("definitive answer, including no match"). |
| C-6 | **Retries cannot double-deduct:** ledger idempotency key `vr:{requestId:N}:x` (extraction) / `vr:{requestId:N}:p` (provider) with the existing unique `(ClientId, IdempotencyKey)` index plus replay handling in `ChargeAsync`; and the charge shares the transaction with the stage result (rolled back together on crash). Re-running the provider stage via `retry-external` charges key `…:p` once, only if the previous provider stage was not already charged (a `Verified`/`NotVerified` request cannot be retried, §10.2). | Two independent guards. |
| C-7 | **Charge failure after work** (`LICENSE_INSUFFICIENT_BALANCE`, license expired/suspended between accept and completion): extraction → request `Failed`/`CREDITS_UNAVAILABLE`, extracted data **not persisted** and document scheduled for purge; provider → result withheld, `ExternalVerification` stays `NotRequested` with reason `PROVIDER_STAGE_UNFUNDED`. | M4-M2 (never deliver for free). |
| C-8 | **Concurrent overdraw is impossible** because `TryConsumeAsync` is the single conditional `UPDATE` (existing, tested by `MeteringTests` 1,000-way). | Unchanged. |
| C-9 | **Goodwill refunds** (e.g. customer dispute) use the existing admin `POST /admin/licensing/transactions/{id}/refund` → ledger `Refund` row with `ReferenceTransactionId`, unique per consumption. The refund row keeps `Operation`. | No new mechanism. |
| C-10 | **Upcoming modules cannot charge:** no `MeteredOperation` exists for them; `IVerificationMeter` rejects any stage whose descriptor lists no operations. | REQ §12. |

### 8.2 New metered operations and proposed defaults (seed as platform-default `CostRule`, policy `OnCompleted`; product owner to confirm, Q8)
| `MeteredOperation` | Stage | Default credits |
|---|---|---|
| `BankStatementExtraction` | Extraction | 5 |
| `PanExtraction` | Extraction | 1 |
| `PanProviderCheck` | Provider | 2 |
| `PassportExtraction` | Extraction | 2 |
| `PassportProviderCheck` | Provider | 3 |

The existing `CostRuleResolver.Fallback` (1 credit) must not silently price these; Database seeds the five rules and a unit test asserts every `MeteredOperation` has a seeded platform rule.

### 8.3 Resulting billing matrix (default policy)
| Scenario | Extraction credits | Provider credits |
|---|---|---|
| Completed, no review, provider `Verified` | charged | charged |
| Completed, review required, provider not configured | charged | 0 |
| Completed, provider `NotVerified` | charged | charged (definitive) |
| Provider `Inconclusive` / `ProviderError` / `Timeout` | charged | 0 (retry later; charged on first definitive result) |
| Unreadable / too many pages / OCR unavailable / worker failed after retries / cancelled / expired | 0 | 0 |
| Insufficient credits at preflight | rejected 402, nothing stored | – |

### 8.4 Atomic commit sequence per stage (inside `IUnitOfWork.ExecuteInTransactionAsync`)
`ChargeStageAsync` → (on failure return the error, nothing written) → persist `Result`/`Checks`/flags → set `CreditsCharged`, `LicenseTransactionId` (or `ProviderLicenseTransactionId`) → `Transition(...)` + `Events` row → `IAuditService.Record` → `IWebhookPublisher.PublishAsync("verification.completed" …)` → `SaveChanges`. Order mirrors `FaceRecognitionService.CommitAsync`.

### 8.5 Traceability: ledger ↔ request
* `LicenseTransactions.RecognitionRequestId` (existing nullable Guid, no FK, included in the row hash) carries the **verification request id**; `Operation` identifies the stage and therefore the source kind. Rationale: adding a column would change the hash serialisation and break verification of every existing chain; ids are v7 GUIDs unique across tables (ADR-22). Database/Backend may later rename the C# property to `SourceRequestId` keeping the column name (hash input is the value, unchanged). The client-facing `LicenseTransactionDto.RecognitionRequestId` is unchanged; the admin ledger UI can resolve the operation name.
* Back-links on the request row: `LicenseTransactionId` and `ProviderLicenseTransactionId` (bigint), `CreditsCharged`, `ProviderCreditsCharged`.
* Reconciliation test: for each client, `SUM(-Credits)` of `Consume` rows with the five operations (minus refunds) equals `SUM(CreditsCharged + ProviderCreditsCharged)` of its requests.
* Dashboards/usage reports aggregate by `Operation`; Backend checks `DashboardQueries` and `UsageReports` tolerate and label the new operations (currently framed as recognitions).

---

## 9. Data model (schema `verification`; Database agent finalises types and writes the migration `M14DocumentVerification`)

Conventions as `docs/02` §1: Guid v7 keys, `ClientId` first in every non-unique index, enums as `varchar(30)` + CHECK, `datetime2(3)` UTC, `RowVersion` on mutable aggregates. All tenant tables implement `ITenantOwned`/`IStrictTenantOwned` so the EF filter, write guard and generated RLS cover them automatically (`TenantModelRules` fails the build otherwise).

### 9.1 Tables
| Table | Tenant interface | Columns (key ones) |
|---|---|---|
| `verification.Requests` | `ITenantOwned` (platform may read metadata, **contains no personal data**) | `Id` (= request id), `ClientId`, `Type`, `Status`, `UploadState`, `ExtractionState`, `RulesState`, `ConsistencyState`, `ExternalState`, `ExternalProviderId` varchar(50) null, `ExternalReference` varchar(100) null, `ExternalCompletedAt` null, `ReviewRequired` bit, `ReviewReasonCodesJson` nvarchar(500) (codes only), `Assurance` varchar(30), `ErrorCode` varchar(50) null, `Source` (`Portal|Api`), `SubmittedByType` (`User|ApiKey`), `SubmittedById` Guid null, `ExternalRef` nvarchar(100) null (caller's own reference — documented as "do not put personal data here"; validated, never logged), `ConsentReference` nvarchar(100) null, `IdempotencyKey` varchar(100) null, `DocumentSha256` binary(32) null, `PageCount` int null, `CreditsCharged` int, `LicenseTransactionId` bigint null, `ProviderCreditsCharged` int, `ProviderLicenseTransactionId` bigint null, `Attempts` int, `ProviderAttempts` int, `NextAttemptAt` datetime2 null, `LeaseToken` Guid null, `LeaseUntil` null, `CancelRequestedAt` null, `CorrelationId` varchar(64), `IpAddress` varchar(45) null (blanked after `PersonalMetadataDays`), `CreatedAt`, `StartedAt`, `CompletedAt`, `UpdatedAt`, `ExpiresAt` (document/result purge time), `PurgedAt` null, `RowVersion`. |
| `verification.Documents` | `IStrictTenantOwned` | `Id`, `ClientId`, `RequestId` (FK, unique in R1), `ContentType` varchar(100), `Extension` varchar(10), `SizeBytes` int, `PageCount`, `Sha256` binary(32), `PurgeAfter`, `PurgedAt` null. |
| `verification.DocumentBlobs` | `IStrictTenantOwned` | `DocumentId` PK/FK, `ClientId`, `Content varbinary(max)` — ciphertext from `IClientEncryption` (nonce + tag inside the payload). Kept apart from `Documents` so list queries never touch LOBs (`LOB` in its own filegroup possible). |
| `verification.Results` | `IStrictTenantOwned` | `RequestId` PK/FK, `ClientId`, `SchemaVersion`, `DataEnc varbinary(max)` (encrypted JSON of `ExtractedData`, purpose `verification-result:{requestId:N}`), `IdentifierMasked` nvarchar(32) null (PAN/passport/account masked, last 4/3 only), `IdentifierHash` binary(32) null (`IPlatformCrypto.ComputeMac("verification-subject", normalised identifier)`; dedupe/velocity only, not reversible without the platform key), `FieldMatchesJson` (outcomes only), `RetainUntil`. |
| `verification.Checks` | `IStrictTenantOwned` | `Id` bigint identity, `ClientId`, `RequestId`, `Ordinal`, `Code` varchar(60), `Stage`, `Outcome`, `Severity`, `Field` varchar(60) null, `Message` nvarchar(300) (template text, numbers/indices only, **no personal values**; enforced by builder API + unit test). |
| `verification.Events` | `ITenantOwned`, `IAppendOnly` | `Id` bigint identity, `ClientId`, `RequestId`, `FromStatus`, `ToStatus`, `Stage` null, `NoteCode` varchar(60) null, `ActorType`, `ActorId` null, `CreatedAt`. Append-only trigger like the ledger. No PII. |
| `verification.DownloadGrants` | `ITenantOwned` | `Id`, `ClientId`, `RequestId`, `Kind` (`Report|TransactionsCsv|OriginalDocument`), `Format`, `PrincipalId`, `TokenHash` binary(32), `ExpiresAt`, `UsedAt` null, `CreatedAt`. Purged after 1 day. |
| `verification.ModuleSettings` | global (no `ClientId`) | `ModuleKey` varchar(40) PK, `IsEnabled` bit, `UpdatedAt`, `UpdatedBy`, `RowVersion`. Seeded: `bank-statement`, `pan`, `passport` = 1. No rows for Upcoming modules. |
| Existing | – | `licensing.CostRules` (+5 seeded rows), CHECK constraints on every column of type `MeteredOperation` (`CostRules`, `ClientCostRules`, `LicenseTransactions`) widened for the five new values; `iam` permission rows synced from code at start-up. |

### 9.2 Indexes
`Requests`: `(ClientId, CreatedAt DESC)` INCLUDE `Type, Status`; `(ClientId, Type, Status, CreatedAt DESC)`; **unique filtered** `(ClientId, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL`; claim `(Status, NextAttemptAt) INCLUDE (ClientId) WHERE Status = 'Queued'`; lease `(Status, LeaseUntil) WHERE Status = 'Processing'`; retention `(ExpiresAt) WHERE PurgedAt IS NULL`; `(ClientId, DocumentSha256)` (duplicate-upload hint). `Results`: `(ClientId, IdentifierHash)` filtered not null. `Checks`: `(ClientId, RequestId, Ordinal)`. `Events`: `(ClientId, RequestId, Id)`. `DownloadGrants`: unique `TokenHash`; `(ExpiresAt)`. Admin stats read `Requests` only (no joins to strict tables).

### 9.3 Row-level security and isolation
Generated automatically from the model: `Requests`, `Events`, `DownloadGrants` use the tenant-or-platform predicate; `Documents`, `DocumentBlobs`, `Results`, `Checks` use the **strict** predicate, so even a Super Admin or a platform-scope job cannot read documents or extracted data. The worker therefore *must* run each request in tenant scope (platform scope only for the claim statement over `Requests`, which holds no PII). Admin "investigation" reads `Requests` + `Events` only. Architecture/Infrastructure tests: cross-tenant read/write blocked by EF filter, write guard and raw SQL under RLS for each new table; platform scope sees `Requests` but gets zero rows from `Documents`/`Results`/`Checks`.

### 9.4 Retention, deletion, minimisation
* Document ciphertext: deleted `DocumentDays` (default 7) after the request reaches a terminal state (or at once on `DELETE …/data`, on `Failed` after 24 h, or when charge failed). Results + Checks: `ResultDays` (90). `Requests` metadata + `Events`: `MetadataDays` (730; contains no personal data; needed for billing/audit). `IpAddress` blanked after `PersonalMetadataDays` (90, same as faces). Hard maxima enforced by the options validator; a client may only shorten via a client setting (future).
* `VerificationRetentionSweeper` (patterned on `FaceRetentionSweeper`: lists clients in platform scope, deletes inside each tenant scope, batch-drains, `Enabled` flag, `BatchSize`, `MaxBatchesPerClient`, hourly) plus purge of expired `DownloadGrants`.
* Erasure: `DELETE /api/v1/verifications/{id}/data` (`verification.erase`) deletes document, result, checks immediately and sets `PurgedAt`; audited with ids only. Client off-boarding: crypto-shredding via `IClientEncryption.DestroyKeysAsync` already makes all ciphertext unreadable; Backend adds the verification tables to the tenant hard-purge routine.
* Minimisation: full PAN/passport/account numbers are **never persisted outside the encrypted result blob**; masked value + keyed hash are the queryable columns. Transactions live only in the encrypted blob (no relational copy). Provider raw payloads are not stored. No document bytes, extracted text, descriptions or amounts in logs, exceptions, ProblemDetails, audit values or metrics labels (`[Sensitive]` on DTO members + `SensitiveDataDestructuringPolicy` + redaction tests).
* Backups: the SQL backup contains ciphertext only; backup retention exceeds the retention windows, so erasure from backups relies on crypto-shredding. DevOps documents this in `docs/runbooks/backup-restore.md` (note for the DPIA).

---

## 10. API contract (summary here; authoritative route table in `docs/03` §7d)

Conventions: `/api/v1`, JSON camelCase, enums as strings, Problem Details with `code` + `correlationId`, other-tenant ids -> **404**, `Result<T>` -> `ToActionResult`, FluentValidation filter, `CancellationToken` everywhere. Client routes accept **JWT or API key** (like `/faces`; integrators are first-class); admin routes are JWT, platform roles. Tenant always from the credential.

### 10.1 Permission keys (add to `Contracts/Identity/Permissions.cs`; `All` entries)
Client scope: `verification.bank-statement.submit`, `verification.pan.submit`, `verification.passport.submit`, `verification.read` (modules catalogue, list, status, result, transactions), `verification.report.download` (report/CSV links), `verification.document.download` (original document; additionally gated by `Verification:Download:AllowOriginalDownload`), `verification.erase`. Platform scope: `verification.admin.read` (module status, stats, tenant metadata investigation, audit view), `verification.admin.configure` (module on/off). Cost editing reuses `licenses.cost-rules`. Audit view reuses `audit.read` for the generic viewer plus `verification.admin.read` for the module filter.
Role defaults: **Super Admin** gets both platform keys (existing rule: all non-client keys). **Client Admin** gets all client keys (existing rule: all non-platform keys). **Client User gets none** in R1 (identity/financial documents; explicit decision, Q9) — `SystemRoles.ClientUserPermissions` is unchanged. API keys: scopes subset of creator's permissions as today.

### 10.2 Routes (client)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/verifications/modules` | `verification.read` | Catalogue: Available + Upcoming + effective status + caller-permitted + limits (max bytes, extensions, max pages) + provider configured flag (boolean only). Upcoming rows have `available:false`. |
| POST | `/verifications/bank-statements` | `verification.bank-statement.submit` | multipart: `document` + `SubmitBankStatementRequest` fields. `[Throttle(VerificationSubmit)]`, `Idempotency-Key` optional. `202` + `Location`, replay `200`. |
| POST | `/verifications/pan` | `verification.pan.submit` | multipart (`document` optional if `panNumber`) |
| POST | `/verifications/passports` | `verification.passport.submit` | multipart (`document` required) |
| GET | `/verifications` | `verification.read` | `type, status, reviewRequired, externalState, from, to, search (request id prefix or externalRef), page, pageSize, sort` |
| GET | `/verifications/{id}` | `verification.read` | `VerificationResultDto` |
| GET | `/verifications/{id}/status` | `verification.read` | `VerificationStatusDto` (cheap; polling target) |
| GET | `/verifications/{id}/transactions` | `verification.read` | bank statements: `page,pageSize<=500` |
| POST | `/verifications/{id}/cancel` | submit key of the request's type | `Queued/Processing` only, else 409 `VERIFICATION_NOT_CANCELLABLE` |
| POST | `/verifications/{id}/retry-external` | submit key of the type | Allowed when `ExternalState ∈ {ProviderNotConfigured, ProviderError, Timeout, Inconclusive}`, request terminal, data not purged, provider now configured; else 409 `VERIFICATION_NOT_RETRYABLE`. Returns `202`. Throttled. |
| DELETE | `/verifications/{id}/data` | `verification.erase` | Immediate purge of document + result + checks. |
| POST | `/verifications/{id}/downloads` | `verification.report.download` (`…document.download` for `OriginalDocument`) | Body `{kind: "Report"|"TransactionsCsv"|"OriginalDocument", format?: "json"|"html"}` -> `{downloadUrl, expiresAt}`. `[Throttle(VerificationDownload)]`, audited. |
| GET | `/verifications/downloads/{token}` | same permission + token | Streams the file (see 10.6). |

### 10.3 Routes (platform admin, JWT)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/admin/verifications/modules` | `verification.admin.read` | Descriptors incl. Upcoming; effective status; `isEnabled`; providers `[{providerId, type, configured, last24h:{verified,notVerified,inconclusive,providerError,timeout}}]` (no secrets); resolved platform-default cost per operation. |
| PUT | `/admin/verifications/modules/{key}` | `verification.admin.configure` | `{isEnabled, reason, rowVersion}`; 404 for unknown/Upcoming keys; audited `verification.module_enabled|disabled`. |
| GET | `/admin/verifications/stats?from&to&type&clientId` | `verification.admin.read` | volumes by type/status, review rate, failures by `ErrorCode`, provider outcomes by state, credits charged (extraction/provider), p50/p95 processing seconds, queue depth and oldest-queued age. `[Throttle(Dashboards)]`. |
| GET | `/admin/verifications/stats/export.csv` | `verification.admin.read` | `CsvStreamResult`, `[Throttle(Exports)]`. |
| GET | `/admin/clients/{clientId}/verifications` and `/{id}` | `verification.admin.read` | **Metadata only** (type, status, stage flags, error code, credits, timings, correlation id, events); never documents, extracted values, masked identifiers or checks. Each detail view is audited (`verification.admin_viewed`). |
| GET | `/admin/verifications/audit?from&to&page` | `audit.read` | Platform audit rows with `Action` starting `verification.` (module switches, cost-rule changes involving the five operations, admin views). Needed because no platform-wide audit endpoint exists yet (STATUS M8a gap 1). |
| (existing) | `/admin/cost-rules…` | `licenses.cost-rules` | Per-type credit costs; operation names extended. |

### 10.4 DTO field lists (Contracts.Verification)
* `SubmitBankStatementRequest` (form fields besides file `document`): `externalRef?` (<=100), `consentReference` (required unless disabled, <=100), `accountHolderName?` (<=150), `bankName?` (<=100), `accountNumberLast4?` (`^\d{4}$`), `periodFrom?`, `periodTo?` (dates, from <= to).
* `SubmitPanRequest`: `externalRef?`, `consentReference`, `panNumber?`, `fullName` (required), `dateOfBirth?`, `fatherName?`; rule: `document` or `panNumber` present.
* `SubmitPassportRequest`: `externalRef?`, `consentReference`, `passportNumber?`, `fullName?`, `dateOfBirth?`, `nationality?` (ISO 3166 alpha-3), `dateOfIssue?`, `dateOfExpiry?`; `document` required.
* `VerificationAcceptedDto`: `requestId, type, status, createdAt, statusUrl, pollAfterSeconds, replayed`.
* `VerificationStatusDto`: `requestId, type, status, stages{uploaded, extracted, rulesChecked, consistencyChecked, externalVerification, externalProviderId?}, reviewRequired, assurance, errorCode?, errorMessage?, createdAt, startedAt?, completedAt?, creditsCharged, correlationId`.
* `VerificationResultDto`: status block + `externalRef?` + `document{kind, pageCount, sizeBytes, purged}` + `summary{passed, warned, failed, notEvaluated}` + `checks[{code, stage, outcome, severity, field?, message}]` + `reviewReasons[{code, message}]` + `fieldMatches[{field, outcome}]` + `external{state, providerId?, reference?, completedAt?, message?}` + `extracted` (exactly one of `bankStatement|pan|passport`, null when purged or not extracted) + `credits{extraction{charged, transactionId?}, provider{charged, transactionId?}}` **only when the caller also holds `license.read`** + `processing{startedAt, completedAt, attempts}`.
* `BankStatementExtractedDto`: `bankName?, accountHolderName?, accountNumberMasked?, currency, periodFrom?, periodTo?, openingBalance?, closingBalance?, transactionCount, totalCredits?, totalDebits?, textSource, ruleSetId, transactions[{index, date, valueDate?, description, reference?, debit?, credit?, balance?}]` (first `InlineTransactions`; `transactionsTruncated`).
* `PanExtractedDto`: `panMasked, holderTypeCode?, holderType?, nameOnCard?, fatherName?, dateOfBirth?`.
* `PassportExtractedDto`: `passportNumberMasked, issuingCountry, nationality, surname, givenNames, dateOfBirth, sex, dateOfIssue?, dateOfExpiry, isExpired, mrz{present, format:"TD3", checks{documentNumber, dateOfBirth, dateOfExpiry, personalNumber, composite: "Valid|Invalid|NotPresent"}}`.
* `VerificationListItemDto`: `requestId, type, status, reviewRequired, externalState, assurance, identifierMasked?, externalRef?, createdAt, completedAt?, creditsCharged`.
* `VerificationModuleDto`: `key, displayName, description, status, available, requiredPermissions[], providerConfigured?, limits{maxFileBytes, extensions[], maxPages}`.
* `DownloadLinkDto`: `downloadUrl, expiresAt`.
* No DTO exposes OCR scores, name-match scores, raw provider payloads, full identifiers or stack traces.

### 10.5 Error codes (add to `Contracts.ErrorCodes`; Problem Details)
| HTTP | `code` | When |
|---|---|---|
| 400 | `VALIDATION_FAILED` | field errors (existing) incl. "document or panNumber required" |
| 400 | `DOCUMENT_REQUIRED` · `DOCUMENT_INVALID` · `DOCUMENT_UNSUPPORTED_TYPE` | missing file; unreadable/corrupt structure, signature≠extension/MIME; type not allowed |
| 402 / 403 | existing `LICENSE_*` | preflight |
| 404 | `NOT_FOUND` · `VERIFICATION_MODULE_DISABLED` | other tenant's id; module off |
| 409 | `VERIFICATION_NOT_CANCELLABLE` · `VERIFICATION_NOT_RETRYABLE` · `CONFLICT` | state conflicts; concurrent identical submit |
| 410 | `DOWNLOAD_LINK_EXPIRED` | expired/used grant (invalid token -> 404) |
| 413 | `DOCUMENT_TOO_LARGE` | over `MaxFileBytes` (Kestrel limit -> `PAYLOAD_TOO_LARGE`) |
| 422 | `DOCUMENT_TOO_MANY_PAGES` · `DOCUMENT_ENCRYPTED` · `DOCUMENT_ACTIVE_CONTENT` | found at inspection; nothing stored or charged |
| 429 | `RATE_LIMITED` · `DAILY_QUOTA_EXCEEDED` · `VERIFICATION_QUEUE_FULL` | throttles |
| 503 | `PROCESSING_UNAVAILABLE` | inspector bulkhead full (load shedding, `Retry-After`) |
Request-level failure codes stored on the request (not HTTP): `DOCUMENT_UNREADABLE`, `OCR_UNAVAILABLE`, `PROCESSING_FAILED`, `PROCESSING_TIMEOUT`, `CREDITS_UNAVAILABLE`, `CANCELLED`, `EXPIRED`. Provider outcomes are **not** HTTP errors: the result is `200` with `external.state`.

### 10.6 Downloads and reports
* Report formats: JSON (`VerificationReportDto` = result + generated-at + disclaimer text), printable HTML (`text/html`, self-contained, `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'`, `Content-Disposition: attachment`), bank-statement transactions CSV (via the CSV neutraliser). PDF report is out of R1 (needs a licence-reviewed PDF writer; open item). The disclaimer is fixed: *"This report records what was read from the document and which checks ran. It does not certify that the document is genuine unless External verification shows Verified by a named provider."*
* **Link design:** `POST …/downloads` creates a `DownloadGrant` (random 256-bit token, SHA-256 stored, TTL `Download:LinkSeconds` = 60 s, bound to `ClientId`, `RequestId`, `Kind`, and the creating principal). `GET /verifications/downloads/{token}` still requires normal authentication and the same permission, checks the grant belongs to the caller and the tenant, consumes it with one conditional `UPDATE … WHERE UsedAt IS NULL AND ExpiresAt > now` (single use), responds `Cache-Control: no-store`, `X-Content-Type-Options: nosniff`, and audits `verification.report_downloaded`. There are **no public or unauthenticated URLs**; no storage path or blob key is ever exposed. The portal BFF requests the link and relays the stream (existing CSV/invoice relay pattern, same-origin check, per-session throttle).
* Original document download is off by default; when enabled it needs `verification.document.download`, is audited, and returns the decrypted original with `Content-Disposition: attachment` and the detected (never the declared) content type.

### 10.7 Rate limits
Existing layers apply (per-IP, per-key, daily quota). New per-principal throttles via `[Throttle]` (shared counters): `ThrottlePolicies.VerificationSubmit` (`Throttle:VerificationSubmitsPerMinute`, 20), `ThrottlePolicies.VerificationDownload` (30). `retry-external` uses the submit policy. Plus `Verification:Limits:MaxQueuedPerClient`. Credits are the economic brake: unpaid work is bounded by preflight + queue cap.

### 10.8 Webhook and audit
Webhook event `verification.completed` (subscribable): `{requestId, type, status, reviewRequired, externalVerification, assurance, creditsCharged, balanceAfter}` — no personal data. Published inside the completing transaction (outbox semantics as recognitions). Audit actions (constants in `Application/Verification/VerificationAuditActions.cs`): `verification.submitted`, `verification.completed`, `verification.failed`, `verification.cancelled`, `verification.external_retry`, `verification.report_downloaded`, `verification.document_downloaded`, `verification.erased`, `verification.module_enabled`, `verification.module_disabled`, `verification.admin_viewed`. Audit values carry ids, type, status, credit numbers only. Log event ids: Backend reserves a free range (proposal 9100–9199; grep first), message templates take ids/codes only.

---

## 11. UI / information architecture (additions to `docs/06` §6)

### 11.1 Client portal
Nav group **Document verification** (after Recognition), items trimmed by permission (`NavigationCatalog.Client`):
| Item | Route | Permission | Icon |
|---|---|---|---|
| Overview | `client/verification` | `verification.read` | `FactCheck` |
| Bank statement | `client/verification/bank-statement` | `verification.bank-statement.submit` | `AccountBalance` |
| PAN | `client/verification/pan` | `verification.pan.submit` | `CreditCard` |
| Passport | `client/verification/passport` | `verification.passport.submit` | `Flight` |
| Verification history | `client/verification/history` | `verification.read` | `History` |

* **Upcoming presentation:** the **Overview** page renders the catalogue from `GET /verifications/modules`: Available modules as cards with a primary action; Upcoming modules (Aadhaar, Driving Licence, Voter ID, DigiLocker, Additional government documents, KYC and fraud detection) as visually muted, non-focusable cards with an `Upcoming` chip, text "Not available yet. No credits are used.", no link, `aria-disabled="true"`. They are **not** sidebar entries (avoids dead links and clutter; `NavItem.Implemented=false` "Soon" pattern is not used for them). A module reported `Disabled` shows "Temporarily unavailable".
* **Submission pages** (one per type, shared `VerificationSubmitPage` layout): `DocumentUpload` (file picker/drag-drop, shows accepted types/size/pages from the catalogue limits, client-side pre-check of extension, size and magic bytes using a `DocumentSniffer` similar to `ImageSniffer`; streams into a bounded buffer, wiped after the request; progress bar; submit disabled while uploading), form fields with the same rules as the API (inline errors mapped from `errors`), consent reference field, cost hint ("This check uses about N credits", from the caller's `license.read` + cost info when authorised), result of submit = navigate to the details page (`202`) which polls. One `Idempotency-Key` per logical submit, kept across retries (as M8b-04). PAN page supports "number only" mode. No JS alerts; snackbar + `ErrorState` with correlation id.
* **History** (`DataTable<VerificationListItemDto>`): columns Request ID (short, copy), Type, Submitted, Status (`VerificationStatusChip`), Review flag, Provider verification (`ExternalStateChip`), Reference, Credits (when authorised). **Row action menu** (`MudMenu`): *View details*, *Download report* (JSON/HTML; plus *Transactions CSV* for bank statements), *Retry provider check* (when allowed), *Cancel* (when allowed), *Delete data* (confirm dialog, type-to-confirm), no duplicate sidebar entries. Filters: type, status, review required, provider state, date range, search. Mobile: card list.
* **Details** (`client/verification/{id:guid}`): header with request id + status; `StageStepper` showing the five flags ("Uploaded", "Extracted", "Rules checked", "Consistency checked", "Official verification") with explicit states incl. "Not requested", "Provider not configured", "Pending"; `ReviewReasonsAlert`; **Official verification card** (provider, state, reference) — the only place the word "verified" may appear as a positive; `CheckResultsTable` (code -> plain text, outcome icon + text, never colour alone); `FieldMatchList`; `ExtractedFieldsCard` (masked identifiers; bank transactions in a paged table); credits card (`ChargeSummary` reuse) when authorised; timestamps and correlation id; polling with `PeriodicTimer` (2 s, then 5 s, `ApiCallOptions.Passive`, stops at terminal state, survives errors, disposes cleanly).
* **Wording table (single source, one static class `VerificationText`):** `Completed + AssuranceLevel ExtractedOnly/ChecksCompleted` -> "Checks completed. Not officially verified."; `ExternalVerification=Verified` -> "Officially verified by {provider}"; `ProviderNotConfigured` -> "Official verification is not available for your account"; `NotVerified` -> "The provider could not confirm these details"; `Inconclusive` -> "The provider's answer was inconclusive"; `ProviderError/Timeout` -> "Official verification could not be completed. You can retry."; `ReviewRequired` -> "Needs review"; no "authentic/genuine/valid/active" strings anywhere (bUnit text-scan test).
* Responsive: sidebar -> drawer `< md`, tables -> cards on `xs`, touch targets >= 44 px, `aria-live` for status changes, keyboard operable menus.

### 11.2 Platform admin portal (`AdminLayout`)
New group **Verification**: `admin/verification` (Overview: stat cards for volume, success/failure/review rate, provider errors, credits consumed, queue depth; `ChartCard` trend; **Modules table**: module, status incl. Upcoming rows, enable/disable switch with reason `FormDialog` (`verification.admin.configure`), provider configured badge per provider id and last-24 h outcome counts, resolved credit cost per operation with a link to `admin/cost-rules` for edits), `admin/verification/requests` (tenant-scoped investigation: `ClientPicker` required, metadata-only list, detail with stage flags, error code, timings, events, credits; banner "Documents and extracted data are not visible to platform staff"), `admin/verification/audit` (audit history of module/cost/investigation actions). Permissions: `verification.admin.read` / `.configure`; nav `Cost rules` page gets the new operations (read from the API list, no hard-coded names).

### 11.3 New shared components (UI agent owns; reuse first)
`DocumentUpload`, `DocumentSniffer`, `VerificationStatusChip`, `ExternalStateChip`, `StageStepper`, `CheckResultsTable`, `FieldMatchList`, `ReviewReasonsAlert`, `ExtractedFieldsCard`, `UpcomingModuleCard`, `ModuleCard`, `VerificationRowMenu`, `VerificationText`. Existing: `PageHeader`, `DataTable`, `FilterBar`, `StatusChip`, `StatCard`, `ChartCard`, `ConfirmDialog`, `FormDialog`, `ErrorState`, `EmptyState`, `SkeletonTable`, `DetailList`, `ClientPicker`, `ChargeSummary`, `Can`.

### 11.4 Typed clients
`IVerificationApiClient` (client calls incl. multipart submit via `ApiGateway` prepared content, status, result, list, cancel, retry, erase, downloads) and `IVerificationAdminApiClient`, in `Services/Api/VerificationApiClients.cs`, with `Ui:UseStubClients` stubs. Permission constants go into `Contracts.Identity.Permissions` first and `WebPermissions` references them (avoids the M12 local-constants deviation).

---

## 12. Security risk register (to be reviewed by the Security agent; findings go to `docs/reviews/security-m14.md`)

| # | Risk | Mitigation (design) |
|---|---|---|
| S1 | Polyglot / disguised files (PDF with image header, HTML as .pdf, SVG) | Allow-list by **magic bytes** (`%PDF-`, JPEG, PNG, WebP RIFF); extension and declared MIME must agree with the detected kind else `DOCUMENT_UNSUPPORTED_TYPE`; no SVG/TIFF/GIF/Office; content is never executed or served inline; downloads use detected type + `nosniff` + `attachment`. |
| S2 | PDF/zip/decompression bombs, page bombs, huge images | Byte cap before reading (Kestrel + `[RequestSizeLimit]`), `MaxPages` enforced at inspection, `MaxImagePixels` before full decode (Skia codec header read), inspector 5 s timeout + bulkhead (503 when full), OCR/raster per-page timeouts and `MaxConcurrent`, worker `Processing:TimeoutSeconds` with kill, memory-bounded rasterisation (DPI cap, one page at a time). Residual: PdfPig has no internal decompression budget -> process-level container memory limit (DevOps) and `MaxFileBytes`. |
| S3 | Active PDF content (JavaScript, launch, embedded files, XFA, external references) | Rejected with `DOCUMENT_ACTIVE_CONTENT`; documents are never rendered in a viewer; rasterisation uses PDFium without JS. Original download is `attachment` only. |
| S4 | Malicious image exploiting codecs | Skia/PDFium are native code: run decode only after size/pixel checks, within the process bulkhead; keep packages patched (Dependabot, vulnerable-package CI step); Tesseract isolated out-of-process; consider container seccomp default. |
| S5 | Command injection via OCR CLI | Argument list only, fixed binary path from config, no user data in arguments (image over stdin), output parsed as TSV, kill on timeout, env cleaned. |
| S6 | ReDoS in bank rule sets / PAN regex from config | Regex match timeout 250 ms, start-up compile validation, rule files are deploy-time config, not tenant-supplied. |
| S7 | SSRF through providers | URLs from configuration only; https; fixed host; no redirects; no request data in URLs; resolved-IP check as the webhook guard does if the base URL is configurable per environment. |
| S8 | PII exposure in logs/audit/metrics/errors | `[Sensitive]` DTO members, destructuring policy, log templates with ids/codes only, check messages built from templates without values, audit values exclude PII, ProblemDetails generic, redaction tests that scan captured logs after a full run with a canary PAN/passport/account/transaction string. |
| S9 | Tenant isolation / IDOR on request ids, grants, blobs | `ITenantOwned`/strict + RLS for every table, 404 for foreign ids, grant bound to client+request+principal, platform cannot read strict tables, worker runs in tenant scope, cross-tenant tests for every endpoint including downloads and `retry/cancel/erase`. |
| S10 | Replay / duplicate submission / double billing | Derived idempotency key + unique index, ledger idempotency key per stage, single-use short-lived download grants, `retry-external` guarded by state. |
| S11 | Cost / abuse attacks (flooding uploads, forcing paid provider calls) | Preflight credits, per-principal throttles, per-key rate/daily quota, `MaxQueuedPerClient`, charge-on-completion, provider stage only after successful extraction and only if configured, circuit breaker. |
| S12 | Stored-data theft (DB dump) | Document and result ciphertext under per-client keys (AES-256-GCM, purpose + request id bound), master-key keyring and rotation already in place, TDE per `docs/04`, short retention, crypto-shred on offboarding; masked values + keyed hash only in queryable columns. |
| S13 | Insider/platform staff access | Strict RLS; admin reads metadata only; every admin detail view audited; `verification.admin.*` separate from client keys; MFA for staff already enforced. |
| S14 | Poisoned/malicious bank-rule or OCR output (injection into UI/CSV) | All extracted text is data: encoded by Blazor, CSV neutraliser on exports, HTML report encodes every value, no `MarkupString`. |
| S15 | Fabricated verification results | Rule V-1, domain invariant, no simulated provider in production code, fakes only in `tests/`, `ProviderNotConfigured` default, bUnit/reflection wording tests. |
| S16 | Worker crash/poison document loops | Lease + attempts cap -> `Failed`; per-document timeout; errors logged with ids only; poison counted. |
| S17 | Timing/enumeration of PAN/passport holders | Results returned only to the submitting tenant; `IdentifierHash` is keyed and never returned; no cross-tenant dedupe lookups exposed. |
| S18 | Legal/regulatory (consent, DPDP/GDPR, cross-border to vendors) | `consentReference` required, retention caps, erasure endpoint, processor/controller wording, vendor DPA is a go-live item (Q4). Not a code control; legal review required. |
| S19 | Download link leakage | Single use, 60 s, authenticated, bound to principal, `no-store`, never logged (token redacted by the log policy; Backend adds `verifications/downloads/*` to the redaction list). |
| S20 | Supply chain (new native libs: PDFium, Tesseract) | CI `dotnet list package --vulnerable`, Trivy image scan, SBOM, pinned versions, licence review (§6.1). |

---

## 13. Test plan mapped to REQ §14

| REQ §14 item | Tests (layer) |
|---|---|
| Valid/invalid requests, all three modules | Validator unit tests (`Application.UnitTests/Verification/*Validators`); API integration: 202 happy path ×3, 400 for each field rule, missing document, PAN number-only, passport without document. |
| OCR success, unreadable, malformed files | `FakeOcrProvider` + synthetic samples: digital PDF (no OCR), scanned PNG via fake OCR, blank page -> `DOCUMENT_UNREADABLE`/Failed not billed, truncated PDF/PNG -> 400 `DOCUMENT_INVALID`, OCR unavailable -> `OCR_UNAVAILABLE`. Real Tesseract smoke test marked `[Trait("Category","Ocr")]`, skipped when the binary is absent (not verified in sandbox). |
| Bank arithmetic and consistency | Table-driven `BankStatementChecks` tests: continuity, row arithmetic, ordering asc/desc/mixed, duplicates, period bounds, missing data -> `NotEvaluated`, rounding/tolerance, lakh-grouped amounts, Dr/Cr suffix; rule-set loader (invalid file fails, ReDoS timeout). |
| PAN format and field mismatches | `PanRules` (valid/invalid lengths, lower case, spaces, every holder type, unknown 4th letter, OCR repair), `NameMatcher` table tests, field-level match outcomes API test; masked PAN only in responses. |
| Passport expiry, MRZ, mismatches | `MrzCheckDigit` with ICAO 9303 specimen (the standard's published example lines — Backend must source and cite), each check digit independently corrupted, composite digit, OCR-repair warning, century pivot, expired/future dates, VIZ-vs-MRZ mismatch, TD1 unsupported. |
| Provider success/failure/timeout/unconfigured | `FakePanVerificationProvider`/`FakePassportVerificationProvider` through the worker on real SQL: Verified, NotVerified, Inconclusive, ProviderError, Timeout, `ProviderNotConfigured` (default build, no fakes), circuit breaker open, retry-external, billing per matrix 8.3, `Verified` impossible without provider reference (domain test). |
| Authorization failures, cross-tenant | Per endpoint: 401, 403 per permission, API key with/without scope, client B gets 404 on A's id for status/result/transactions/cancel/retry/erase/downloads; platform staff cannot read documents/results/checks (RLS test) but can read `Requests`; admin endpoints 403 for client tokens. |
| Upload security and limits | Polyglot (PNG header + `.pdf`), extension/MIME mismatch, oversize (413), page cap, pixel cap, encrypted PDF, PDF with `/JavaScript`/`/Launch`/`/EmbeddedFile`, zero-byte, double extension, path-traversal file names (never used for storage), slow upload floor, inspector bulkhead 503. |
| Concurrent credit deduction and idempotent retries | 50 parallel submissions on a license with credits for 10 -> exactly 10 charged, rest `CREDITS_UNAVAILABLE`/402, never negative (reuse `MeteringTests` approach); same `Idempotency-Key` ×10 in parallel -> one request, one document, one ledger row; worker re-claim after simulated crash mid-stage -> exactly one ledger row (`vr:{id}:x`); `retry-external` twice in parallel -> one provider charge; reconciliation test (§8.5); ledger verification still passes. |
| Status transitions and audit | Exhaustive state-machine tests (allowed and forbidden), `Events` rows per transition, append-only trigger test, audit rows contain no PII (canary), cancel while Processing, expiry of old Queued, lease expiry re-queue, poison -> Failed. |
| API contracts, UI validation, navigation, responsive | OpenAPI/route snapshot vs `docs/03` §7d; ProblemDetails shape and codes; bUnit: nav trimming per permission, Upcoming cards non-interactive, upload pre-checks, history row menu actions, details stepper states for every `ExternalState`, wording scan (no "authentic/genuine"), polling stops at terminal state/dispose, 360 px layout classes; portal-to-API e2e for one submit with fakes (pattern of `PortalBffEndToEndTests`). |
| Architecture | `DependencyRuleTests` extended: `src` does not reference `TestSupport`; no type named `Fake*`/`Simulated*` in verification production namespaces; Upcoming descriptors have empty permissions/operations/providers and no route; only `Infrastructure.Verification` references PdfPig/PDFtoImage/Process; controllers do not reference EF; no `IgnoreQueryFilters` in verification code; every `MeteredOperation` has a seeded cost rule; DTO reflection test for forbidden member names (`isAuthentic`, `isGenuine`, `isValid`, `isVerified`). |
| Release checks (REQ §14 tail) | `dotnet build -c Release` of the solution, `dotnet ef migrations has-pending-model-changes` clean, RLS readiness check includes the new tables, full test run recorded in STATUS. |

---

## 14. ADRs (decisions; also listed in `docs/01` ADR table)

| # | Decision | Rationale / trade-off |
|---|---|---|
| ADR-17 | **Module catalogue/registry** (`IVerificationModule` descriptors in code + `verification.ModuleSettings` switch). Upcoming = data only. | New modules plug in without touching licensing/auth/tenant/audit; "Upcoming cannot bill or have endpoints" becomes testable. Trade-off: descriptor changes need a deploy (acceptable). |
| ADR-18 | **Asynchronous processing** (202 + request id + worker + status/result + webhook). | OCR/vendor latency, retries, crash safety. Trade-off: polling and more states. |
| ADR-19 | **New `IDocumentStorageService` with encrypted content in SQL Server** (`verification.DocumentBlobs`) using `IClientEncryption`. No existing blob store. | Transactional with the request row, tenant RLS applies, multi-node safe without a shared volume, covered by backup/restore and key rotation. Trade-off: DB growth (mitigated by 10 MB cap and 7-day retention), LOB I/O; the interface permits Azure Blob/S3/file system later. |
| ADR-20 | **Separated outcome model + Rule V-1** (assurance computed; only a provider `Verified` is "verified"). | Meets REQ §3 and prevents false assurance. Trade-off: more fields/UI states. |
| ADR-21 | **Charge on completion per stage; no up-front consume or hold; no system refunds needed.** Manual review is charged, failures and provider failures are not. | Atomic with state change, consistent with faces, no parallel balance mechanism. Trade-off: small window where accepted work may find credits gone (bounded by queue cap; handled by C-7). |
| ADR-22 | **Reuse `LicenseTransactions.RecognitionRequestId` for the verification request id**; discriminate by `Operation`. | Changing the ledger row shape breaks the hash chain of all existing licenses. Trade-off: misleading column name (documented, rename later at C# level only). |
| ADR-23 | **New `MeteredOperation` members per stage** and seeded platform cost rules. | Reuses cost resolution/admin UI. Trade-off: enum + CHECK constraint migration. |
| ADR-24 | **Tesseract via CLI behind `IOcrProvider`; PdfPig for digital text; PDFtoImage for rasterisation (versions to verify).** | Permissive licences, crash isolation, replaceable. Trade-off: external binary in the image, pre-1.0 PdfPig API, OCR accuracy unverified. |
| ADR-25 | **Personal data only in strict-tenant tables, encrypted; requests table is PII-free; masked value + keyed hash for queries.** | Platform staff can investigate without seeing PII. Trade-off: admin investigation is metadata-only. |
| ADR-26 | **No Polly/resilience package; job-level retry + per-call timeout + simple circuit breaker.** | Zero new dependency, retries are observable and idempotent. Trade-off: hand-rolled breaker to maintain (small). |
| ADR-27 | **No vendor adapter in R1; `NotConfigured` providers only; no simulated provider outside tests.** | Never fabricate results; vendor unknown. Trade-off: external verification delivers value only after follow-up. |
| ADR-28 | **Routes `/api/v1/verifications/...` accept JWT or API key** (like `/faces`); admin under `/api/v1/admin/verifications`. | Integrators are first-class; one code path. |
| ADR-29 | **Short-lived single-use authenticated download grants**; no public URLs; original-document download off by default; report formats JSON/HTML/CSV in R1. | PII minimisation. Trade-off: no PDF report yet. |
| ADR-30 | **Client User role gets no verification permission by default.** | Identity and financial documents; least privilege. Trade-off: admins grant via role changes (custom roles are not yet creatable — see Q9). |

---

## 15. Open questions for the product owner (defaults assumed; none blocks Design/Backend start)

| # | Question | Default assumed |
|---|---|---|
| Q1 | Which **approved PAN verification provider** (name, API contract, sandbox + production credentials, data-processing agreement)? | None. `ProviderNotConfigured` until named. |
| Q2 | Which **approved passport verification provider**? (For Indian passports there is no public third-party API that we can verify; a commercial KYC vendor is likely.) | None. |
| Q3 | Is any **bank statement authenticity/analysis provider** required (e.g. account-aggregator, bureau)? | None in R1: `NotRequested`. |
| Q4 | Vendor and cross-border data-transfer approval, DPA, and whether PII may leave the deployment at all. | Local processing only. |
| Q5 | Which **banks** must have first-class rule sets, and can we get **anonymised, consented sample statements** (and sample PAN/passport images) for tuning? | `generic` rule set only. |
| Q6 | OCR **accuracy acceptance criteria** and languages (English only? Hindi/Devanagari?). | English; no accuracy gate asserted. |
| Q7 | Is Tesseract (Apache-2.0) in the container acceptable, and is poppler (GPL) forbidden as a fallback? | Tesseract yes, poppler no without legal sign-off. |
| Q8 | **Credit prices** per stage (defaults in §8.2) and whether manual-review and `NotVerified` should be charged. | As §8.2/8.3. |
| Q9 | Should **Client User** (or a new role) be able to submit/read verifications? Custom client roles cannot yet be created via the API. | Client Admin only. |
| Q10 | Retention periods (document 7 d, result 90 d, metadata 2 y) and legal hold requirements; DPIA owner. | As §5. |
| Q11 | PDF report required for R1? | JSON/HTML/CSV only. |
| Q12 | Is `consentReference` mandatory for all three types? | Yes (`Verification:RequireConsentReference`). |
| Q13 | Is TD3-only MRZ acceptable (other nationalities' ID cards TD1)? | TD3 only. |

---

## 16. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| No vendor, no credentials, no real sample documents | "Official verification" and OCR accuracy cannot be proven in this environment | Honest defaults (`ProviderNotConfigured`), contract-first adapter checklist, QA accuracy pass with owner-supplied samples before go-live; module cannot be marked production-ready (REQ §14 last paragraph). |
| PDFtoImage/SkiaSharp version compatibility, PdfPig pre-1.0 | Build/runtime friction | Spike task P2 before building on them (§17); wrappers; fallbacks listed in §6.1. |
| Native OCR/PDFium in hardened containers (read-only FS, non-root) | Runtime failures | DevOps adds tmpfs and package, health `IsAvailable`, `/health/ready` reports OCR as degraded-not-fatal. |
| Scope size (3 modules, async pipeline, 2 portals) | Schedule | Foundation first, PAN/passport after, parsers in parallel; strict ownership (§17). |
| Large `varbinary(max)` in the primary DB | Size, backup time | Caps and short retention; storage interface allows later move. |
| Dashboards written for "recognitions" | Misleading charts | Backend audit of `DashboardQueries`/`UsageReports` with new operations; label by module. |
| Race between accept-time preflight and completion-time charge | Rare `CREDITS_UNAVAILABLE` | Queue cap, early worker preflight, clear client message. |
| Regulatory exposure for identity/financial data | Legal | Retention caps, erasure, consent field, legal review (Q4, Q10). |

---

## 17. Work breakdown, ownership and merge plan

### 17.1 Agents and exclusive folders (no two agents edit the same file)

| Agent | Exclusive ownership (create/edit) |
|---|---|
| **Architect** (this step, done) | `docs/01,03,04` (M14 sections), `docs/07`, ADR rows. Later: answers disputes; updates `docs/03` if contracts change (only the Architect edits these). |
| **Database (DB)** | `src/Infrastructure/Persistence/Configurations/Verification*.cs`, `Persistence/Repositories/Verification*.cs`, `Persistence/Queries/Verification*.cs`, `Persistence/Migrations/*M14DocumentVerification*`, seed of cost rules and `ModuleSettings` (`Persistence/Seed/Verification*.cs`), RLS/append-only registration for the new tables, `docs/02` (M14 section finalisation), SQL scripts under `deploy/sql/` if any. |
| **Backend-A (foundation + API + bank statement wiring)** | `src/Domain/Verification/**`, `src/Contracts/Verification/**`, `src/Application/Verification/{Abstractions,Modules,Submission,Processing,Query,Admin,Billing}/**`, `src/Application/Verification/VerificationApplicationRegistration.cs`, `src/Infrastructure/Verification/{Storage,Background,Platform}/**` and `VerificationInfrastructureRegistration.cs`, `src/Api/Controllers/Verification/**`, `src/Api/Controllers/Admin/VerificationAdminController.cs`, unit tests `tests/Domain.UnitTests/Verification/**` and `tests/Application.UnitTests/Verification/{Submission,Processing,Query,Billing}/**`. Sole editor of the shared source files listed in 17.3. |
| **Backend-B (document processing, parsers, modules)** | `src/Application/Verification/{BankStatements,Pan,Passport,Matching}/**` (pure logic + the three `IModuleProcessor`/`IVerificationModule` implementations: `BankStatementModule*`, `PanModule*`, `PassportModule*`), `src/Application/Verification/Parsing/**` (`DocumentText` models), `src/Infrastructure/Verification/{Documents,Ocr,Providers}/**`, `config/bank-rules/*.json`, unit tests `tests/Application.UnitTests/Verification/{BankStatements,Pan,Passport,Matching,Parsing}/**` and `tests/Infrastructure.UnitTests/Verification/**`. Each exposes one registration extension in its own folder (`AddVerificationParsing()`, `AddVerificationDocuments()`, `AddVerificationProviders()`); Backend-A calls them from the single registration file. |
| **UI-A (client portal + shell)** | `src/Web/Blazor/Pages/Client/Verification/**`, `Components/Verification/**` (client components), `Services/Api/VerificationApiClients.cs` (client interface/impl/stub), BFF relay for downloads, **and the shared Web files in 17.3** (`NavigationCatalog.cs`, `WebPermissions.cs`, Web DI registration), `tests/Web.ComponentTests/Verification/Client*`. |
| **UI-B (admin portal)** | `src/Web/Blazor/Pages/Admin/Verification/**`, `Components/Verification/Admin*/**`, admin client interface/impl in `Services/Api/VerificationAdminApiClients.cs`, `tests/Web.ComponentTests/Verification/Admin*`. (May be the same agent as UI-A, in sequence.) Needs UI-A's nav commit first. |
| **Security reviewer** | `docs/reviews/security-m14*.md`; may add tests under `tests/**/Verification/Security*`; reports only, no product code. |
| **QA** | `tests/TestSupport/Verification/**` (fakes, sample generators), `tests/Api.IntegrationTests/Verification/**`, `tests/Infrastructure.IntegrationTests/Verification/**`, `tests/Architecture.Tests/Verification*.cs`, `ApiFactory` extensions in `tests/Api.IntegrationTests/Support/ApiFactoryVerificationExtensions.cs` (new file; `ApiFactory.cs` itself is not edited), accuracy-pass notes in `docs/reviews/qa-m14.md`. |
| **Code reviewer** | `docs/reviews/code-m14*.md`, no product code. |
| **DevOps** | `Directory.Packages.props`, `*.csproj` package references and `Directory.Build.props`, `deploy/**`, `.github/**`, Dockerfiles (Tesseract package, tmpfs), `deploy/CONFIG.md` ("Document verification (M14)" section), runbooks, `docs/release-checklist.md`, health-check wiring request to Backend-A (Backend-A adds the check class; DevOps documents). |
| **Orchestrator** | `docs/STATUS.md` (only), merges, gate sign-off. |

### 17.2 Order and merge points

```
P0  Architect (done)                         → docs/07 etc.
MP1 Backend-A "contract skeleton" commit  (small, first, ~day 1):
      Domain/Verification types + enums + state machine, Contracts/Verification, ErrorCodes, Permissions (+WebPermissions mirror by UI-A),
      MeteredOperation members, Application abstractions (§4 signatures) with no behaviour, webhook event constant.
      → everyone else branches from MP1.
MP1.5 DevOps: Directory.Packages.props + csproj PackageReferences (PdfPig, PDFtoImage after the P2 spike), Dockerfile OCR package,
      CONFIG.md skeleton. Backend-B's spike P2 (version/compat/licence check, sandbox restore) gates this.
Parallel after MP1:
  DB        : configurations, repositories, migration, seeds, RLS checks (against the Domain types from MP1)
  Backend-A : submission core, storage, worker, query, billing meter, controllers, retention sweeper
  Backend-B : parsers/rules/matchers (pure, unit-tested), document pipeline, OCR provider, NotConfigured providers, module processors
  UI-A      : nav + typed clients with stubs (against Contracts), pages with stub clients
  QA        : fakes, sample generators, test host extensions, architecture tests (red until implementation lands)
MP2 DB migration merged → Backend-A runs integration tests against real SQL.
MP3 Backend-B merged into main line → Backend-A wires `IModuleProcessor`s; bank statement end-to-end (accept → worker → result → charge) green.
MP4 PAN + passport end-to-end (module processors already merged by Backend-B; Backend-A only configuration/registration) + admin API.
MP5 UI-A complete against the real API (stubs off) ; UI-B admin screens (needs MP4 admin API).
MP6 Security review (gate) → fixes by owners → re-verify.
MP7 QA full suites (unit, integration, API, UI) + accuracy pass notes.
MP8 Code review → fixes.
MP9 DevOps: CONFIG.md, runbooks, image build with OCR verified, CI; STATUS DevOps gate.
MP10 Final regression (full solution Release build + all tests), orchestrator marks gates.
```
Backend split rationale: Backend-B's work is pure functions and infrastructure adapters with no shared files; it can start the moment the interfaces exist (MP1). Backend-A owns every file that wires modules into licensing, API, worker and DI, so integration conflicts cannot occur. PAN and passport module processors are written by Backend-B directly after the bank statement parser, in the same folders, so "foundation + bank statement" (Backend-A with Backend-B's bank parser) lands first and PAN/passport follow without touching foundation files.

### 17.3 Shared files — one owner, one merge step each
| File | Single owner | When / how |
|---|---|---|
| `src/Api/Program.cs` | Backend-A | Expected unchanged (hosted services via DI). If the per-route size limit or a policy needs it, Backend-A only, after MP2. |
| `src/Application/DependencyInjection.cs`, `src/Infrastructure/DependencyInjection.cs` | Backend-A | One line each at MP1/MP3 calling `AddVerificationApplication()` / `AddVerificationInfrastructure()` (files owned by Backend-A); Backend-B's `Add…` calls sit inside Backend-A's registration files, so B never touches the global DI files. |
| `src/Contracts/Identity/Permissions.cs` (catalogue + `All`; `SystemRoles` unchanged) | Backend-A | MP1, single commit (nine keys, §10.1). |
| `src/Contracts/Common/ErrorCodes.cs` | Backend-A | MP1. |
| `src/Domain/Licensing/Enums.cs` (`MeteredOperation`), `CostRuleResolver.Fallback` | Backend-A | MP1; DB writes seeds/CHECK migration afterwards. |
| `src/Domain/Api/Webhooks.cs` (event constant) | Backend-A | MP1. |
| `src/Application/Api/ApiKeySupport.cs` (`ThrottlePolicies`) and `src/Infrastructure/Platform/PrincipalThrottle.cs` (`ThrottleOptions`) | Backend-A | With the controllers. |
| `Directory.Packages.props`, `*.csproj` | DevOps | MP1.5, on written request in the PR description of Backend-B's spike; nobody else edits. |
| `deploy/CONFIG.md`, `docs/runbooks/*`, `docs/release-checklist.md` | DevOps | MP9; Backend agents hand over a "config delta" list in their report. |
| `docs/STATUS.md` | Orchestrator | After each gate. |
| `docs/01,03,04` | Architect | Contract change requests go to the Architect; Backend/UI never edit them. |
| `docs/02` | DB (finalises) | Architect's planned section is the starting point. |
| `docs/06`, `docs/ui-notes.md` | UI-A (ui-notes), Architect (06 structure) | UI notes appended under "M14". |
| `src/Web/Blazor/Services/NavigationCatalog.cs`, `NavModels.cs`, `WebPermissions.cs`, Web service registration | UI-A | First UI commit; UI-B rebases on it. |
| `tests/Api.IntegrationTests/Support/ApiFactory.cs` | QA | Existing file untouched; extensions in a new file. |
| `tests/Architecture.Tests/DependencyRuleTests.cs` | QA | New rules in a new file `VerificationArchitectureTests.cs`. |

### 17.4 Definition of done additions for M14 (also in `docs/05`)
All standard DoD items, plus: Rule V-1 tests green; ledger reconciliation test green; every new table covered by the RLS readiness check; redaction canary test green; Upcoming-module guarantees test green; provider-dependent items listed as outstanding in STATUS (not "Complete" while the PAN/passport providers are unconfigured and accuracy is unmeasured, REQ §14).

### 17.5 Follow-ups by agent (summary)
* **Database:** schema §9, migration `M14DocumentVerification`, CHECK widening for `MeteredOperation`, five cost-rule seeds, `ModuleSettings` seed, append-only trigger for `Events`, RLS regeneration/readiness, indexes §9.2, retention queries.
* **Backend-A / Backend-B / UI / Security / QA / Code review / DevOps:** as in 17.1–17.3; Security must specifically review S1–S20 and the PDF/OCR sandboxing; DevOps must confirm Tesseract/PDFium/PdfPig in the image (not verified here).

---

## 18. Deviations recorded at MP1 (contract skeleton, Backend-A)

Where the code forced a difference from the text above. Everything else in §2–§10 is implemented as written. Contract-level items need the Architect's confirmation (`docs/03` §7d).

| # | Design | As landed | Why / consequence |
|---|---|---|---|
| D1 | Each module registers its own `IVerificationModule` singleton in its folder | All nine descriptors (3 Available, 6 Upcoming) are data in `Application/Verification/Modules/ModuleCatalogue.cs`, registered by `AddVerificationApplication()` through `CatalogueModule` | One place enforces the Upcoming invariants. Backend-B registers only `IModuleProcessor`s and must **not** register another `IVerificationModule` (duplicate keys fail start-up). |
| D2 | Abstractions and their models live in the owning folders (`BankStatements`, `Matching`, ...) | All interfaces and the records in their signatures live in `Application/Verification/Abstractions/*` (`ModuleProcessing`, `DocumentHandling`, `ModuleLogic`, `Submission`, `ExtractedData`). Backend-B adds implementations, rule sets and the static helpers (`MrzTd3Parser`, `MrzCheckDigit`, `PanRules`) in its own folders | Keeps the MP1 surface in one folder owned by one agent. `BankRuleSet` sub-records, `BankStatementParseResult`, `ProviderResult`, `NameMatch` are minimal and may be extended additively. |
| D3 | `DocumentKind` in Application (§4.3) | `DocumentKind`, `VerificationStage`, `VerificationActorType`, `DownloadKind` are Domain enums (`Domain/Verification/Enums.cs`) | The Domain entities need them. |
| D4 | `IVerificationMeter.ChargeStageAsync(clientId, requestId, stage, outcome)`, returns `MeterTicket` from preflight | `ChargeStageAsync` also takes `VerificationType type`; preflight returns `VerificationMeterTicket(ExtractionCost, ProviderCost, AvailableCredits)` | The operation is derived from type + stage; `Licensing.MeterTicket` already exists with a different shape. |
| D5 | `Transition(to)` | `Transition(to, now, stage?, noteCode?, actorType, actorId)`; the event row (`verification.Events`) is appended to the aggregate (`Events` navigation) in the same call | Events are mandatory per transition (§7.3). `VerificationEvent.FromStatus` is nullable: the creation event has no "from". |
| D6 | `Assurance` computed | Stored property refreshed on every mutation by `VerificationRules.ComputeAssurance` (private setter) | The column exists in §9.1 and is filterable; there is still no public way to set it. |
| D7 | `ReviewReasonCodesJson` column | Same name; the entity also exposes `ReviewReasonCodes` (get-only list, to be ignored by EF) | Domain stays free of EF. |
| D8 | `Uploaded` is `NotStarted|Completed|Failed` | A number-only PAN request (no file) records `UploadState = Skipped` | Truthful; `StageState` already has `Skipped`. |
| D9 | `verification.Requests` has `UpdatedAt` | Inherited from `AuditableEntity` (stamped by persistence) together with `CreatedAt`; no duplicate property | Matches other aggregates. |
| D10 | Registry is a singleton | `IVerificationModuleRegistry` and `IModuleGate` are **scoped** (they read `IVerificationModuleSettingReader`, a persistence port to be implemented by the Database agent). A module with no `ModuleSettings` row is treated as enabled; unknown keys report `Disabled` | Singleton cannot depend on the DbContext. The switch only turns modules off. |
| D11 | Default cost-rule seeds | Defaults live in `Domain/Verification/VerificationOperations.cs` (`DefaultCredits`, `DefaultPolicy`, `For(type, stage)`); `LicensingSeeder` and the CHECK-constraint migration are untouched (Database agent) | Seeds are persistence-owned (§17.1). Unit test asserts all five operations have a default. |
| D12 | Submit validators | `SubmitBankStatementRequestValidator`, `SubmitPanRequestValidator`, `SubmitPassportRequestValidator` take `IOptions<VerificationOptions>` (consent is required only when `Verification:RequireConsentReference`). All consent fields are therefore nullable in the DTOs. "document or panNumber" and "document required" are enforced by the submission core (needs the file) | `Every_request_dto_has_a_fluent_validator` requires a validator per `*Request`. Date fields are `DateOnly?`. |
| D13 | `Verification:Providers:*` | Per provider section (`Pan`, `Passport`) plus `StoreRawResponse`; the validator rejects `StoreRawResponse = true` in release 1. `Retention:DocumentDays <= ResultDays` is also enforced. `Throttle:*` keys arrive with the controllers (not in `VerificationOptions`) | Safer defaults; throttle policies belong to the API step. |
| D14 | Architecture tests in `VerificationArchitectureTests.cs` (QA) | MP1 rules are in `tests/Architecture.Tests/VerificationContractRuleTests.cs`; QA keeps its file | Avoids a same-file conflict (§17.3). |
| D15 | DTO lists (§10.4) | Concrete records in `Contracts/Verification/VerificationDtos.cs`, including admin shapes (`AdminVerificationModuleDto`, `VerificationStatsDto`, `AdminVerificationItemDto`, ...) and query records, which §10 only names. Enum-valued fields are strings | Admin DTOs are first proposals, additive to change before UI-B starts. |

## 18b. Deviations and decisions recorded at MP3/MP4 (Backend-A round two: submission, worker, API, admin)

Everything in §2–§10 not listed here is implemented as written. The numbering continues §18.

| # | Design | As built | Why / consequence |
|---|---|---|---|
| D16 | `Completed/ReviewRequired -> Processing` for retry-external (no lease) | `VerificationRequest.RequeueForExternalRetry`: the finished request goes to **Queued** (`CompletedAt` cleared, `NextAttemptAt = now`, `Attempts = 0`, event `EXTERNAL_RETRY`); the worker claims it under a lease like any request and, because the document-stage flags are already complete, runs only the provider stage. `BeginExternalRetry` and the `Completed -> Processing` transition are gone (`VerificationRules.CanTransition`, tests updated). `ExpireStaleQueuedAsync` now only expires requests whose extraction and rules stages are `NotStarted`, so a re-queued retry (or a crash-recovered request that already delivered a stage) is never expired. | Resolves the Database agent's flagged problem. Residual: if the node dies during a retry and the attempt cap is reached, the lease sweeper ends the request as `Failed/PROCESSING_TIMEOUT` although its result exists (rare; open item). |
| D17 | Submitted fields (needed by the comparison and the provider call) | Kept, with the extracted data, in ONE sealed JSON envelope in `verification.Results.DataEnc` (`SubmissionEnvelope`, purpose `verification-result:{id:N}`). The row is created at accept time through `IVerificationRepository.CreateIdempotentAsync(..., submission)` (new optional parameter) in the same transaction as the request and document, with no identifier columns until extraction. | `Requests` must hold no personal data and no other table existed. Consequences: erasing the result also erases the inputs; a request has a `Results` row from accept (check `Document`/`Extracted` null-ness, not row existence). No schema change. |
| D18 | `IDocumentStorageService.StoreAsync/ReadAsync(documentId)/DeleteAsync(documentId)` | `SealAsync(clientId, requestId, upload, inspection, purgeAfter)` returns the encrypted rows **without saving** (the submission inserts them with the request atomically), `ReadAsync(requestId)`, `DeleteAsync(requestId)`. Implementation `SqlDocumentStorageService` (purpose `verification-document:{requestId:N}`). | Atomic accept, no orphan blob after a lost idempotency race. |
| D19 | Credit policy C-4/C-5 | As §8 with these specifics: a failed extraction still calls the meter with `MeterOutcome.Failed` inside the failing transaction, so an operator-set `OnAttempt` policy bills it and the default `OnCompleted` does not; a refused charge never changes the failure. The provider stage is pre-flighted right before the vendor call (never call a paid vendor that cannot be billed) and its charge happens with the stored answer (a refused charge discards the answer: `PROVIDER_STAGE_UNFUNDED` review reason, `ExternalState` stays `NotRequested`). A PAN submitted as a number only still pays the extraction price for its local checks. | Documented in `docs/03` §7d. |
| D20 | Provider retries inside the job + `ProviderCircuitBreaker` (§4.5) | Not built. A provider failure is recorded (`ProviderError`/`Timeout`), is free, and is retried by the client/operator with `retry-external` (throttled). `Providers:*:MaxAttempts`, `BreakerFailures`, `BreakerOpenSeconds` are validated but unused in release 1. | No vendor adapter exists; a breaker without a vendor cannot be exercised honestly. Add with the first real adapter. |
| D21 | Inspector limits as `422` (§10.5) | `DocumentInspector` returned `400` for page-count, encryption and active-content rejections; changed to `422` to match the contract. | Backend-B file, one-line change per code. |
| D22 | `cancel` / `retry-external` authorised by "the submit key of the request's type" | The route requires `verification.read`; the application service then requires the submit permission of the request's own type through `IPermissionEvaluator` (same policy as `[HasPermission]`, API-key scopes included). `GET downloads/{token}` requires `verification.read` at the route and the kind's download permission in the service. | An attribute cannot express "one of N keys by data". |
| D23 | Webhook and audit for every terminal state | The processor and the lifecycle service publish `verification.completed` and audit. The two set-based sweeps (lease cap `PROCESSING_TIMEOUT`, stale-queue `EXPIRED`) only append the event row. Failed/cancelled-without-delivery requests have their document, inputs and checks erased immediately by the processor (instead of "document 24 h after Failed"); a request ended by a sweep keeps its data until the retention windows. | Open item: publish for sweeper-ended requests (needs the ids back from the SQL statements). |
| D24 | Retention `MetadataDays` | Documents, results (with checks), requests past `ExpiresAt`, expired grants and personal metadata (IP) are purged by `VerificationRetentionSweeper` (`Verification:RetentionJob:*`). `Requests.ExpiresAt` is set to the latest purge time when the request ends (now + max(DocumentDays, ResultDays); `now` for failed/cancelled-undelivered). Request rows and their append-only events are **not** deleted (`MetadataDays` unused): no primitive exists and they are the billing/audit trace. | Open item with the DPIA owner. |
| D25 | Worker options | Added `Verification:Worker:PerClientLimit` (2, per-tenant claim cap) and `:HousekeepingBatch` (100). Retention job options: `Verification:RetentionJob:Enabled|IntervalMinutes|BatchSize|MaxBatchesPerClient|GrantRetentionHours`. Throttles: `Throttle:VerificationSubmitsPerMinute` (20), `Throttle:VerificationDownloadsPerMinute` (30). | DevOps config delta for `deploy/CONFIG.md`. |
| D26 | Metering of verification calls | `ApiUsageMiddleware` applies the per-credential rate limit and daily quota to state-changing (non-GET) `/api/v1/verifications` calls of portal users; API keys are metered on every call as before. | Polling must not exhaust a quota. |
| D27 | API-key scopes | `ApiKeyScopes.Assignable` gained the three submit keys, `verification.read`, `verification.report.download` and `verification.erase`. `verification.document.download` and the platform keys are never assignable. | ADR-28 (integrators are first-class). |
| D28 | HTTP 410 | New `ErrorType.Gone` (`Error.Gone`), mapped to `410` by `ApiProblem`. | `DOWNLOAD_LINK_EXPIRED`. |
| D29 | Persistence ports | Additive: `IVerificationRepository.CreateIdempotentAsync(..., VerificationResult? submission = null)`, `IVerificationModuleSettings.SetExpectedVersion`, `IVerificationAdminQueries.ListPlatformAuditAsync`; `VerificationResult.SetFieldMatches`; `VerificationRequest.SetExpiresAt`. No migration (`has-pending-model-changes` is clean). | |
| D30 | Admin audit of cost-rule changes | Not duplicated: cost-rule edits use the existing cost-rule admin and its audit; `/admin/verifications/audit` lists the `verification.*` actions only. | |
| D31 | Provider status in the admin module list | `providerId` is the id of the resolved provider (`None` while unconfigured); `last24Hours` counts come from the requests' stored provider id. | No vendor id exists in release 1. |

**Config delta for DevOps (`deploy/CONFIG.md`, section "Document verification (M14)"):** `Verification:Worker:PerClientLimit`, `Verification:Worker:HousekeepingBatch`, `Verification:RetentionJob:*`, `Throttle:VerificationSubmitsPerMinute`, `Throttle:VerificationDownloadsPerMinute`, plus everything in §5. Run exactly one `VerificationWorker` per node that should process (`Verification:Worker:Enabled=false` on others).
