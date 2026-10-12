using System.Text;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Contracts.Verification;
using NexaVerify.Web.Components.Verification;

namespace NexaVerify.Web.Services;

/// <summary>
/// In-memory document checks for the design-review stubs (<c>Ui:UseStubClients</c>, Development/UiDemo only). Sample data only: the
/// "Verified" sample names a made-up provider and exists solely so that state can be reviewed visually. Nothing here ever runs in production.
/// </summary>
public sealed class StubVerificationStore(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<StubCheck> _checks = Seed(clock ?? TimeProvider.System);

    public static Guid ClientId { get; } = Guid.Parse("00000000-0000-0000-0000-00000000c001");

    public bool ModulesEnabled { get; set; } = true;

    public Dictionary<string, bool> Enabled { get; } = new() { ["bank-statement"] = true, ["pan"] = true, ["passport"] = true };

    public Dictionary<string, int> Costs { get; } = new()
    {
        ["BankStatementExtraction"] = 5, ["PanExtraction"] = 1, ["PanProviderCheck"] = 2, ["PassportExtraction"] = 2, ["PassportProviderCheck"] = 3,
    };

    public List<(DateTime At, string Action, string Entity)> AuditTrail { get; } = [];

    public IReadOnlyList<StubCheck> All { get { lock (_gate) { return _checks.OrderByDescending(c => c.CreatedAt).ToList(); } } }

    public StubCheck? Find(Guid id) { lock (_gate) { return _checks.FirstOrDefault(c => c.Id == id); } }

    public StubCheck Add(string type, string? externalRef)
    {
        var check = new StubCheck(Guid.NewGuid(), type, _clock.GetUtcNow().UtcDateTime, externalRef);
        lock (_gate) { _checks.Add(check); }
        return check;
    }

    public DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static List<StubCheck> Seed(TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        StubCheck Make(string last, string type, int hoursAgo, string outcome, string? external = null) =>
            new(Guid.Parse($"00000000-0000-0000-0000-00000000e{last}"), type, now.AddHours(-hoursAgo), external) { Outcome = outcome };
        return
        [
            Make("001", VerificationTypes.BankStatement, 26, "completed", "FY25-Q4"),
            Make("002", VerificationTypes.Pan, 5, "pan-notconfigured"),
            Make("003", VerificationTypes.Passport, 3, "passport-error"),
            Make("004", VerificationTypes.Pan, 2, "verified"),
            Make("005", VerificationTypes.Passport, 1, "failed"),
            Make("006", VerificationTypes.BankStatement, 70, "review"),
        ];
    }
}

/// <summary>One stored sample check. Outcome picks the script it follows.</summary>
public sealed class StubCheck(Guid id, string type, DateTime createdAt, string? externalRef)
{
    public Guid Id { get; } = id;

    public string Type { get; } = type;

    public DateTime CreatedAt { get; } = createdAt;

    public string? ExternalRef { get; } = externalRef;

    public string Outcome { get; set; } = "completed";

    public bool Cancelled { get; set; }

    public bool Erased { get; set; }

    public bool RetriedOk { get; set; }
}

public sealed class StubVerificationApiClient(StubVerificationStore store) : IVerificationApiClient
{
    private static readonly string[] Upcoming = ["aadhaar", "driving-licence", "voter-id", "digilocker", "govt-documents", "kyc-fraud"];

    public Task<ApiResult<IReadOnlyList<VerificationModuleInfo>>> GetModulesAsync(CancellationToken ct = default)
    {
        var limits = (int pages) => new VerificationLimitsDto(10 * 1024 * 1024, [".pdf", ".jpg", ".jpeg", ".png", ".webp"], pages);
        VerificationModuleInfo Mod(string key, string name, string description, bool? provider, int pages, int extraction, int? providerCredits) =>
            new(key, name, description, store.Enabled[key] ? "Available" : "Disabled", true, [], provider, limits(pages), null, extraction, providerCredits);
        List<VerificationModuleInfo> list =
        [
            Mod("bank-statement", "Bank statement verification", "Reads a statement, lists the transactions and checks that the balances add up.", null, 30, 5, null),
            Mod("pan", "PAN verification", "Checks the PAN format and compares the card with the details you enter.", false, 2, 1, 2),
            Mod("passport", "Passport verification", "Reads the photo page, checks dates and the machine-readable zone.", false, 4, 2, 3),
        ];
        var names = new Dictionary<string, string>
        {
            ["aadhaar"] = "Aadhaar verification", ["driving-licence"] = "Driving licence verification", ["voter-id"] = "Voter ID verification",
            ["digilocker"] = "DigiLocker integration", ["govt-documents"] = "Additional government documents", ["kyc-fraud"] = "Additional KYC and fraud checks",
        };
        list.AddRange(Upcoming.Select(k => new VerificationModuleInfo(k, names[k], "Planned for a later release.", "Upcoming", false, [], null, null, "Planned")));
        return Task.FromResult(ApiResult<IReadOnlyList<VerificationModuleInfo>>.Ok(list));
    }

    private Task<ApiResult<VerificationAcceptedDto>> Accept(string type, string key, string? externalRef)
    {
        if (!store.Enabled[VerificationTypes.ModuleKey(type)])
        {
            return Task.FromResult(ApiResult<VerificationAcceptedDto>.Fail("VERIFICATION_MODULE_DISABLED", "This check is switched off.", "stub-corr", 404));
        }

        var check = store.Add(type, externalRef);
        check.Outcome = type switch { VerificationTypes.BankStatement => "completed", VerificationTypes.Pan => "pan-notconfigured", _ => "passport-error" };
        return Task.FromResult(ApiResult<VerificationAcceptedDto>.Ok(new VerificationAcceptedDto(check.Id, type, "Queued", check.CreatedAt, $"/api/v1/verifications/{check.Id}/status", 2, false)));
    }

    public Task<ApiResult<VerificationAcceptedDto>> SubmitBankStatementAsync(SubmitBankStatementRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(document.Data.Length);
        return Accept(VerificationTypes.BankStatement, idempotencyKey, request.ExternalRef);
    }

    public Task<ApiResult<VerificationAcceptedDto>> SubmitPanAsync(SubmitPanRequest request, DocumentUploadFile? document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(document?.Data.Length ?? 0);
        return Accept(VerificationTypes.Pan, idempotencyKey, request.ExternalRef);
    }

    public Task<ApiResult<VerificationAcceptedDto>> SubmitPassportAsync(SubmitPassportRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(document.Data.Length);
        return Accept(VerificationTypes.Passport, idempotencyKey, request.ExternalRef);
    }

    private string StatusOf(StubCheck c)
    {
        if (c.Cancelled) { return "Cancelled"; }
        var age = (store.Now - c.CreatedAt).TotalSeconds;
        if (age < 2) { return "Queued"; }
        if (age < 5) { return "Processing"; }
        return c.Outcome switch { "failed" => "Failed", "review" => "ReviewRequired", "passport-error" when !c.RetriedOk => "ReviewRequired", _ => "Completed" };
    }

    private string ExternalOf(StubCheck c, string status)
    {
        if (status is "Queued" or "Processing" or "Cancelled" || c.Type == VerificationTypes.BankStatement) { return ExternalStates.NotRequested; }
        if (c.RetriedOk) { return ExternalStates.Verified; }
        return c.Outcome switch { "verified" => ExternalStates.Verified, "pan-notconfigured" => ExternalStates.ProviderNotConfigured, "passport-error" => ExternalStates.ProviderError, _ => ExternalStates.NotRequested };
    }

    private VerificationStagesDto StagesOf(StubCheck c, string status, string external)
    {
        var done = status is "Completed" or "ReviewRequired";
        var s = done ? StageStates.Completed : status == "Failed" ? StageStates.Failed : StageStates.NotStarted;
        return new VerificationStagesDto(StageStates.Completed, status == "Failed" ? StageStates.Failed : status == "Processing" ? StageStates.Completed : s, s, s, external, external == ExternalStates.Verified ? "sample-provider" : null);
    }

    public Task<ApiResult<VerificationStatusDto>> GetStatusAsync(Guid id, CancellationToken ct = default, bool passive = false)
    {
        if (store.Find(id) is not { } c) { return Task.FromResult(ApiResult<VerificationStatusDto>.Fail("NOT_FOUND", "Not found", "stub-corr", 404)); }
        var status = StatusOf(c);
        var external = ExternalOf(c, status);
        return Task.FromResult(ApiResult<VerificationStatusDto>.Ok(new VerificationStatusDto(c.Id, c.Type, status, StagesOf(c, status, external), status == "ReviewRequired", Assurance(status, external),
            status == "Failed" ? "DOCUMENT_UNREADABLE" : null, null, c.CreatedAt, c.CreatedAt.AddSeconds(2), VerificationStatuses.IsTerminal(status) ? c.CreatedAt.AddSeconds(5) : null, 0, "stub-corr")));
    }

    private static string Assurance(string status, string external) =>
        external == ExternalStates.Verified ? "AuthoritativelyVerified" : status is "Completed" or "ReviewRequired" ? "ChecksCompleted" : "None";

    public Task<ApiResult<VerificationResultDto>> GetResultAsync(Guid id, CancellationToken ct = default)
    {
        if (store.Find(id) is not { } c) { return Task.FromResult(ApiResult<VerificationResultDto>.Fail("NOT_FOUND", "We couldn't find that.", "stub-corr", 404)); }
        var status = StatusOf(c);
        var external = ExternalOf(c, status);
        var done = status is "Completed" or "ReviewRequired";
        var checks = done && !c.Erased ? ChecksFor(c) : [];
        var summary = new VerificationSummaryDto(checks.Count(x => x.Outcome == "Pass"), checks.Count(x => x.Outcome == "Warn"), checks.Count(x => x.Outcome == "Fail"), checks.Count(x => x.Outcome == "NotEvaluated"));
        var reasons = status == "ReviewRequired" ? new List<VerificationReviewReasonDto> { new("CHECK_FAILED", "One or more checks found something worth a look.") } : [];
        if (external is ExternalStates.ProviderError) { reasons.Add(new("PROVIDER_UNAVAILABLE", "The provider could not be reached.")); }
        var providerId = external == ExternalStates.Verified ? "sample-provider" : null;
        return Task.FromResult(ApiResult<VerificationResultDto>.Ok(new VerificationResultDto(
            c.Id, c.Type, status, StagesOf(c, status, external), status == "ReviewRequired", Assurance(status, external), status == "Failed" ? "DOCUMENT_UNREADABLE" : null, null, c.CreatedAt, "stub-corr", c.ExternalRef,
            new VerificationDocumentDto(c.Type == VerificationTypes.BankStatement ? "Pdf" : "Jpeg", c.Type == VerificationTypes.BankStatement ? 4 : 1, 240_000, c.Erased),
            summary, checks, reasons, done ? MatchesFor(c) : [],
            new VerificationExternalDto(external, providerId, providerId is null ? null : "ref-7f3a21", external is ExternalStates.NotRequested or ExternalStates.ProviderNotConfigured or ExternalStates.Pending ? null : c.CreatedAt.AddSeconds(8), null),
            done && !c.Erased ? ExtractedFor(c) : null,
            done ? new VerificationCreditsDto(new StageCreditDto(c.Type == VerificationTypes.BankStatement ? 5 : c.Type == VerificationTypes.Pan ? 1 : 2, 1042), new StageCreditDto(external == ExternalStates.Verified ? 2 : 0, null)) : null,
            new VerificationProcessingDto(c.CreatedAt.AddSeconds(2), done ? c.CreatedAt.AddSeconds(5) : null, 1))));
    }

    private static List<VerificationCheckDto> ChecksFor(StubCheck c) => c.Type switch
    {
        VerificationTypes.BankStatement =>
        [
            new("BS_COMPLETENESS", "Rules", "Pass", "Info", null, "All key details were found."),
            new("BS_ROW_ARITHMETIC", "Consistency", c.Outcome == "review" ? "Fail" : "Pass", c.Outcome == "review" ? "Review" : "Info", null, c.Outcome == "review" ? "The running balance does not add up from row 14." : "The running balance adds up on every row."),
            new("BS_TOTALS", "Consistency", "Pass", "Info", null, "Opening balance, credits and debits give the closing balance."),
            new("BS_DUPLICATES", "Consistency", "Warn", "Warning", null, "2 rows repeat exactly."),
            new("BS_EXTERNAL_NOT_PERFORMED", "External", "NotApplicable", "Info", null, "No official check exists for bank statements."),
        ],
        VerificationTypes.Pan =>
        [
            new("PAN_FORMAT", "Rules", "Pass", "Info", "pan", "The PAN has the expected shape."),
            new("PAN_HOLDER_TYPE", "Rules", "Pass", "Info", null, "The holder type letter is recognised."),
            new("PAN_NAME_MATCH", "Consistency", "Warn", "Warning", "name", "The name is close but not identical."),
            new("EXTERNAL_NOT_PERFORMED", "External", "NotEvaluated", "Info", null, "No official check was performed."),
        ],
        _ =>
        [
            new("PP_MRZ_PRESENT", "Rules", "Pass", "Info", null, "The machine-readable zone was found."),
            new("PP_MRZ_CHECK_COMPOSITE", "Rules", "Pass", "Info", null, "The overall check digit agrees."),
            new("PP_NOT_EXPIRED", "Rules", "Pass", "Info", "dateOfExpiry", "The passport has not expired."),
            new("PP_VIZ_MRZ_CONSISTENT", "Consistency", "Pass", "Info", null, "Printed details agree with the machine-readable zone."),
        ],
    };

    private static List<VerificationFieldMatchDto> MatchesFor(StubCheck c) => c.Type switch
    {
        VerificationTypes.Pan => [new("name", "PartialMatch"), new("dateOfBirth", "Match"), new("fatherName", "NotProvided")],
        VerificationTypes.Passport => [new("name", "Match"), new("dateOfBirth", "Match"), new("nationality", "NotExtracted")],
        _ => [new("accountHolderName", "Match"), new("bankName", "Match"), new("accountNumberLast4", "Mismatch")],
    };

    private static VerificationExtractedDto ExtractedFor(StubCheck c) => c.Type switch
    {
        VerificationTypes.BankStatement => new(new BankStatementExtractedDto("Sample Bank", "Priya Sharma", "XXXXXX4821", "INR", new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31), 125000.50m, 98250.75m, 60, 412000m, 438749.75m,
            "DigitalPdf", "generic", [], false), null, null),
        VerificationTypes.Pan => new(null, new PanExtractedDto("XXXXX1234X", "P", "Individual", "PRIYA SHARMA", "RAJESH SHARMA", new DateOnly(1990, 5, 14)), null),
        _ => new(null, null, new PassportExtractedDto("XXXX567", "IND", "IND", "SHARMA", "PRIYA", new DateOnly(1990, 5, 14), "F", new DateOnly(2019, 8, 1), new DateOnly(2029, 7, 31), false,
            new MrzDto(true, "TD3", new MrzChecksDto("Valid", "Valid", "Valid", "NotPresent", "Valid")))),
    };

    public Task<ApiResult<PagedResult<VerificationListItemDto>>> ListAsync(VerificationListQuery query, CancellationToken ct = default)
    {
        var items = store.All.Select(c =>
        {
            var status = StatusOf(c);
            var external = ExternalOf(c, status);
            var masked = c.Type switch { VerificationTypes.Pan => "XXXXX1234X", VerificationTypes.Passport => "XXXX567", _ => "XXXXXX4821" };
            return new VerificationListItemDto(c.Id, c.Type, status, status == "ReviewRequired", external, Assurance(status, external), masked, c.ExternalRef, c.CreatedAt, VerificationStatuses.IsTerminal(status) ? c.CreatedAt.AddSeconds(5) : null, 0);
        })
        .Where(i => query.Type is null || i.Type == query.Type)
        .Where(i => query.Status is null || i.Status == query.Status)
        .Where(i => query.ReviewRequired != true || i.ReviewRequired)
        .Where(i => query.From is null || i.CreatedAt >= query.From)
        .Where(i => query.To is null || i.CreatedAt <= query.To)
        .ToList();
        var size = Math.Clamp(query.PageSize, 1, 100);
        var page = items.Skip((Math.Max(1, query.Page) - 1) * size).Take(size).ToList();
        return Task.FromResult(ApiResult<PagedResult<VerificationListItemDto>>.Ok(new PagedResult<VerificationListItemDto>(page, Math.Max(1, query.Page), size, items.Count)));
    }

    public Task<ApiResult<PagedResult<BankTransactionDto>>> GetTransactionsAsync(Guid id, VerificationTransactionsQuery query, CancellationToken ct = default)
    {
        var all = Enumerable.Range(1, 60).Select(i => new BankTransactionDto(i, new DateOnly(2026, 1, 1).AddDays(i), null, i % 3 == 0 ? $"SALARY ACME LTD {i}" : $"UPI/MERCHANT {i}", null,
            i % 3 == 0 ? null : 450.25m + i, i % 3 == 0 ? 52000m : null, 125000.50m - i * 100)).ToList();
        var size = Math.Clamp(query.PageSize, 1, VerificationTransactionsQuery.MaxPageSize);
        var page = all.Skip((Math.Max(1, query.Page) - 1) * size).Take(size).ToList();
        return Task.FromResult(ApiResult<PagedResult<BankTransactionDto>>.Ok(new PagedResult<BankTransactionDto>(page, Math.Max(1, query.Page), size, all.Count)));
    }

    public Task<ApiResult<bool>> CancelAsync(Guid id, CancellationToken ct = default)
    {
        if (store.Find(id) is not { } c) { return Task.FromResult(ApiResult<bool>.Fail("NOT_FOUND", "Not found", "stub-corr", 404)); }
        if (!VerificationStatuses.IsInProgress(StatusOf(c))) { return Task.FromResult(ApiResult<bool>.Fail("VERIFICATION_NOT_CANCELLABLE", "This check has already finished.", "stub-corr", 409)); }
        c.Cancelled = true;
        return Task.FromResult(ApiResult<bool>.Ok(true));
    }

    public Task<ApiResult<bool>> RetryExternalAsync(Guid id, CancellationToken ct = default)
    {
        if (store.Find(id) is not { } c) { return Task.FromResult(ApiResult<bool>.Fail("NOT_FOUND", "Not found", "stub-corr", 404)); }
        if (ExternalOf(c, StatusOf(c)) is ExternalStates.ProviderNotConfigured or ExternalStates.NotRequested or ExternalStates.Verified)
        {
            return Task.FromResult(ApiResult<bool>.Fail("VERIFICATION_NOT_RETRYABLE", "This check cannot be retried right now.", "stub-corr", 409));
        }

        c.RetriedOk = true;
        return Task.FromResult(ApiResult<bool>.Ok(true));
    }

    public Task<ApiResult<bool>> EraseAsync(Guid id, CancellationToken ct = default)
    {
        if (store.Find(id) is not { } c) { return Task.FromResult(ApiResult<bool>.Fail("NOT_FOUND", "Not found", "stub-corr", 404)); }
        c.Erased = true;
        return Task.FromResult(ApiResult<bool>.Ok(true));
    }
}

public sealed class StubVerificationDownloads : IVerificationDownloads
{
    public Task<ApiResult<DownloadLinkDto>> CreateLinkAsync(Guid requestId, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct) =>
        Task.FromResult(ApiResult<DownloadLinkDto>.Ok(new DownloadLinkDto("/api/v1/verifications/downloads/" + Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddSeconds(60))));

    private static HttpResponseMessage Respond(string body, string contentType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
    }

    public Task<ApiResult<HttpResponseMessage>> OpenAsync(string downloadUrl, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct)
    {
        var body = kind.ContentType switch
        {
            "text/csv" => "Date,Description,Debit,Credit,Balance\n2026-01-02,SAMPLE ROW,100.00,,900.00\n",
            "application/json" => "{\"sample\":true}",
            _ => "<!doctype html><html><body><h1>Sample report</h1><p>Design-review sample. " + VerificationText.Disclaimer + "</p></body></html>",
        };
        return Task.FromResult(ApiResult<HttpResponseMessage>.Ok(Respond(body, kind.ContentType)));
    }
}

public sealed class StubAdminVerificationApiClient(StubVerificationStore store) : IAdminVerificationApiClient
{
    private static readonly string[] Upcoming = ["aadhaar", "driving-licence", "voter-id", "digilocker", "govt-documents", "kyc-fraud"];

    public Task<ApiResult<IReadOnlyList<AdminVerificationModuleDto>>> GetModulesAsync(CancellationToken ct = default)
    {
        var none = new ProviderOutcomeCountsDto(0, 0, 0, 0, 0);
        AdminVerificationModuleDto Mod(string key, string name, string d, int order, IReadOnlyList<VerificationProviderStatusDto> providers, params string[] ops) =>
            new(key, name, d, store.Enabled[key] ? "Available" : "Disabled", true, store.Enabled[key], order, providers, ops.Select(o => new ModuleCostDto(o, store.Costs[o], "OnCompleted")).ToList(), DateTime.UtcNow.AddDays(-3), "AAAAAAAB");
        List<AdminVerificationModuleDto> list =
        [
            Mod("bank-statement", "Bank statement verification", "Statement reading and consistency checks.", 1, [], "BankStatementExtraction"),
            Mod("pan", "PAN verification", "PAN format, card reading and provider check.", 2, [new("pan", "Pan", false, none)], "PanExtraction", "PanProviderCheck"),
            Mod("passport", "Passport verification", "Passport reading, dates, MRZ and provider check.", 3, [new("passport", "Passport", false, none)], "PassportExtraction", "PassportProviderCheck"),
        ];
        list.AddRange(Upcoming.Select((k, i) => new AdminVerificationModuleDto(k, k, "Planned for a later release.", "Upcoming", false, null, 10 + i, [], [], null, null)));
        return Task.FromResult(ApiResult<IReadOnlyList<AdminVerificationModuleDto>>.Ok(list));
    }

    public Task<ApiResult<bool>> UpdateModuleAsync(string key, UpdateVerificationModuleRequest request, CancellationToken ct = default)
    {
        if (!store.Enabled.ContainsKey(key)) { return Task.FromResult(ApiResult<bool>.Fail("NOT_FOUND", "We couldn't find that.", "stub-corr", 404)); }
        store.Enabled[key] = request.IsEnabled;
        store.AuditTrail.Add((DateTime.UtcNow, request.IsEnabled ? "verification.module_enabled" : "verification.module_disabled", key));
        return Task.FromResult(ApiResult<bool>.Ok(true));
    }

    public Task<ApiResult<VerificationStatsDto>> GetStatsAsync(VerificationStatsQuery query, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<VerificationStatsDto>.Ok(new VerificationStatsDto(
            412, [new("BankStatement", 160), new("Pan", 170), new("Passport", 82)], [new("Completed", 301), new("ReviewRequired", 64), new("Failed", 31), new("Cancelled", 9), new("Expired", 7)], 64,
            [new("DOCUMENT_UNREADABLE", 21), new("PROCESSING_TIMEOUT", 6), new("CREDITS_UNAVAILABLE", 4)],
            [new("ProviderNotConfigured", 252), new("ProviderError", 11), new("Timeout", 3)], 1380, 0, 6.2, 18.4, 2, 12)));

    public Task<ApiResult<PagedResult<AdminVerificationItemDto>>> ListRequestsAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken ct = default)
    {
        var items = store.All.Select(Item).Where(i => query.Type is null || i.Type == query.Type).Where(i => query.Status is null || i.Status == query.Status).ToList();
        var size = Math.Clamp(query.PageSize, 1, 100);
        return Task.FromResult(ApiResult<PagedResult<AdminVerificationItemDto>>.Ok(new PagedResult<AdminVerificationItemDto>(items.Skip((Math.Max(1, query.Page) - 1) * size).Take(size).ToList(), Math.Max(1, query.Page), size, items.Count)));
    }

    private static AdminVerificationItemDto Item(StubCheck c)
    {
        var failed = c.Outcome == "failed";
        var external = c.Type == VerificationTypes.BankStatement ? "NotRequested" : c.Outcome == "pan-notconfigured" ? "ProviderNotConfigured" : c.Outcome == "passport-error" ? "ProviderError" : c.Outcome == "verified" ? "Verified" : "NotRequested";
        return new AdminVerificationItemDto(c.Id, StubVerificationStore.ClientId, c.Type, failed ? "Failed" : "Completed",
            new VerificationStagesDto("Completed", failed ? "Failed" : "Completed", failed ? "NotStarted" : "Completed", failed ? "NotStarted" : "Completed", external, null), false, failed ? "None" : "ChecksCompleted",
            failed ? "DOCUMENT_UNREADABLE" : null, failed ? 0 : 2, 0, 1, "stub-corr-" + c.Id.ToString("N")[..6], c.CreatedAt, c.CreatedAt.AddSeconds(2), c.CreatedAt.AddSeconds(6));
    }

    public Task<ApiResult<AdminVerificationDetailDto>> GetRequestAsync(Guid clientId, Guid requestId, CancellationToken ct = default) =>
        Task.FromResult(store.Find(requestId) is { } c
            ? ApiResult<AdminVerificationDetailDto>.Ok(new AdminVerificationDetailDto(Item(c), [new(null, "Queued", null, "SUBMITTED", "User", c.CreatedAt), new("Queued", "Processing", null, null, "System", c.CreatedAt.AddSeconds(2)), new("Processing", Item(c).Status, null, null, "System", c.CreatedAt.AddSeconds(6))]))
            : ApiResult<AdminVerificationDetailDto>.Fail("NOT_FOUND", "We couldn't find that.", "stub-corr", 404));

    public Task<ApiResult<PagedResult<AuditLogDto>>> GetAuditAsync(ActivityQuery query, CancellationToken ct = default)
    {
        var rows = store.AuditTrail.OrderByDescending(a => a.At).Select((a, i) => new AuditLogDto(i + 1, a.At, a.Action, "VerificationModule", a.Entity, "User", Guid.NewGuid(), null, "stub-corr")).ToList();
        rows.Add(new AuditLogDto(900, DateTime.UtcNow.AddDays(-2), "costrule.set", "CostRule", "PanExtraction", "User", Guid.NewGuid(), null, "stub-corr"));
        return Task.FromResult(ApiResult<PagedResult<AuditLogDto>>.Ok(new PagedResult<AuditLogDto>(rows, 1, 25, rows.Count)));
    }

    public Task<ApiResult<CostRuleDto>> SetDefaultCostAsync(string operation, int credits, string chargePolicy, string? reason, CancellationToken ct = default)
    {
        store.Costs[operation] = credits;
        store.AuditTrail.Add((DateTime.UtcNow, "costrule.set", operation));
        return Task.FromResult(ApiResult<CostRuleDto>.Ok(new CostRuleDto(Guid.NewGuid(), "Platform", null, null, operation, credits, chargePolicy, DateTime.UtcNow, null)));
    }
}
