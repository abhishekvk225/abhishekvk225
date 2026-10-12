using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Contracts.Verification;

namespace NexaVerify.Web.ComponentTests;

public static class VerificationSample
{
    public static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-00000000e001");
    public static readonly DateTime Now = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    public static VerificationLimitsDto Limits(int maxBytes = 10 * 1024 * 1024, int pages = 30) => new(maxBytes, [".pdf", ".jpg", ".jpeg", ".png", ".webp"], pages);

    public static IReadOnlyList<VerificationModuleInfo> Modules(string[]? disabled = null, bool? panProvider = false, int maxBytes = 10 * 1024 * 1024) =>
    [
        new("bank-statement", "Bank statement verification", "Reads statements.", disabled?.Contains("bank-statement") == true ? "Disabled" : "Available", true, [], null, Limits(maxBytes), null, 5, null),
        new("pan", "PAN verification", "Checks PANs.", disabled?.Contains("pan") == true ? "Disabled" : "Available", true, [], panProvider, Limits(maxBytes, 2), null, 1, 2),
        new("passport", "Passport verification", "Reads passports.", disabled?.Contains("passport") == true ? "Disabled" : "Available", true, [], false, Limits(maxBytes, 4), null),
        new("aadhaar", "Aadhaar Verification", "Planned.", "Upcoming", false, [], null, null, "Planned"),
        new("driving-licence", "Driving Licence Verification", "Planned.", "Upcoming", false, [], null, null, "Planned"),
        new("voter-id", "Voter ID Verification", "Planned.", "Upcoming", false, [], null, null, "Planned"),
        new("digilocker", "DigiLocker Integration", "Planned.", "Upcoming", false, [], null, null, "Planned"),
        new("govt-documents", "Additional Government Document Verification", "Planned.", "Upcoming", false, [], null, null, "Planned"),
        new("kyc-fraud", "Additional KYC and fraud detection", "Planned.", "Upcoming", false, [], null, null, "Planned"),
    ];

    public static VerificationStagesDto Stages(string external = "NotRequested", string? provider = null, string extracted = "Completed") =>
        new("Completed", extracted, "Completed", "Completed", external, provider);

    public static VerificationResultDto Result(string type = "Pan", string status = "Completed", string external = "ProviderNotConfigured", bool review = false, string? provider = null,
        bool credits = false, VerificationExtractedDto? extracted = null, bool purged = false, string? errorCode = null) =>
        new(Id, type, status, Stages(external, provider), review, external == "Verified" ? "AuthoritativelyVerified" : "ChecksCompleted", errorCode, null, Now, "corr-1", "REF-9",
            new VerificationDocumentDto("Pdf", 2, 1000, purged), new VerificationSummaryDto(2, 1, 1, 1),
            [
                new("PAN_FORMAT", "Rules", "Pass", "Info", "pan", "The PAN has the expected shape."),
                new("PAN_NAME_MATCH", "Consistency", "Warn", "Warning", "name", "The name is close."),
                new("BS_ROW_ARITHMETIC", "Consistency", "Fail", "Review", null, "Balance differs from row 14."),
                new("EXTERNAL_NOT_PERFORMED", "External", "NotEvaluated", "Info", null, "No official check."),
            ],
            review ? [new VerificationReviewReasonDto("CHECK_FAILED", "A check failed.")] : [],
            [new VerificationFieldMatchDto("name", "PartialMatch"), new VerificationFieldMatchDto("dateOfBirth", "Match")],
            new VerificationExternalDto(external, provider, provider is null ? null : "ref-1", provider is null ? null : Now, null),
            purged ? null : extracted ?? new VerificationExtractedDto(null, new PanExtractedDto("XXXXX1234X", "P", "Individual", "PRIYA SHARMA", null, new DateOnly(1990, 5, 14)), null),
            credits ? new VerificationCreditsDto(new StageCreditDto(1, 11), new StageCreditDto(2, 12)) : null,
            new VerificationProcessingDto(Now, Now.AddSeconds(5), 1));

    public static VerificationStatusDto Status(string status = "Completed", string external = "NotRequested") =>
        new(Id, "Pan", status, Stages(external), false, "ChecksCompleted", null, null, Now, Now, VerificationStatuses.IsTerminal(status) ? Now : null, 0, "corr-1");

    public static VerificationListItemDto Item(string type = "Pan", string status = "Completed", string external = "ProviderNotConfigured", Guid? id = null) =>
        new(id ?? Id, type, status, status == "ReviewRequired", external, "ChecksCompleted", "XXXXX1234X", "REF-9", Now, Now, 1);

    public static VerificationAcceptedDto Accepted(int poll = 2) => new(Id, "Pan", "Queued", Now, "/api/v1/verifications/x/status", poll, false);

    public static AdminVerificationModuleDto AdminModule(string key = "pan", bool enabled = true, bool providerConfigured = false) =>
        new(key, "PAN verification", "PAN checks.", enabled ? "Available" : "Disabled", true, enabled, 2,
            [new VerificationProviderStatusDto("pan", "Pan", providerConfigured, new ProviderOutcomeCountsDto(3, 1, 0, 2, 1))],
            [new ModuleCostDto("PanExtraction", 1, "OnCompleted"), new ModuleCostDto("PanProviderCheck", 2, "OnCompleted")], Now, "RV1");

    public static AdminVerificationModuleDto AdminUpcoming(string key = "aadhaar") =>
        new(key, "Aadhaar Verification", "Planned", "Upcoming", false, null, 10, [], [], null, null);

    public static VerificationStatsDto Stats() =>
        new(100, [new("Pan", 60), new("Passport", 40)], [new("Completed", 80), new("ReviewRequired", 12), new("Failed", 8)], 12, [new("DOCUMENT_UNREADABLE", 8)],
            [new("ProviderError", 3), new("Timeout", 1)], 300, 20, 4.5, 12, 2, 65);

    public static AdminVerificationItemDto AdminItem(Guid? client = null) =>
        new(Id, client ?? Guid.Parse("00000000-0000-0000-0000-00000000c001"), "Pan", "Failed", Stages("NotRequested", extracted: "Failed"), false, "None", "DOCUMENT_UNREADABLE", 0, 0, 2, "corr-admin", Now, Now, Now);
}

public sealed class FakeVerificationApi : IVerificationApiClient
{
    public List<string> Calls { get; } = [];

    public List<string> Keys { get; } = [];

    public List<(string Name, bool Passive)> StatusCalls { get; } = [];

    public int StatusCount => StatusCalls.Count;

    public DocumentUploadFile? LastDocument { get; private set; }

    public object? LastRequest { get; private set; }

    public VerificationListQuery? LastQuery { get; private set; }

    public Func<Task<ApiResult<IReadOnlyList<VerificationModuleInfo>>>> Modules { get; set; } = () => Ok.Of(VerificationSample.Modules());

    public Func<int, Task<ApiResult<VerificationAcceptedDto>>> Submit { get; set; } = _ => Ok.Of(VerificationSample.Accepted());

    /// <summary>Answer per status call number (1-based).</summary>
    public Func<int, Task<ApiResult<VerificationStatusDto>>> Status { get; set; } = _ => Ok.Of(VerificationSample.Status());

    public Func<Task<ApiResult<VerificationResultDto>>> Result { get; set; } = () => Ok.Of(VerificationSample.Result());

    public Func<VerificationListQuery, Task<ApiResult<PagedResult<VerificationListItemDto>>>> List { get; set; } = _ => Task.FromResult(Ok.Page(VerificationSample.Item()));

    public Func<VerificationTransactionsQuery, Task<ApiResult<PagedResult<BankTransactionDto>>>> Transactions { get; set; } = q =>
        Task.FromResult(ApiResult<PagedResult<BankTransactionDto>>.Ok(new PagedResult<BankTransactionDto>(
            [new BankTransactionDto(1, new DateOnly(2026, 1, 2), null, "SALARY ACME", null, null, 52000m, 52000m)], q.Page, q.PageSize, 120)));

    public Func<Task<ApiResult<bool>>> Action { get; set; } = () => Ok.Of(true);

    private int _submits;

    public Task<ApiResult<IReadOnlyList<VerificationModuleInfo>>> GetModulesAsync(CancellationToken ct = default)
    {
        Calls.Add("modules");
        return Modules();
    }

    private Task<ApiResult<VerificationAcceptedDto>> Do(string name, object request, DocumentUploadFile? document, string key, IProgress<long>? progress)
    {
        Calls.Add(name);
        Keys.Add(key);
        LastRequest = request;
        LastDocument = document is null ? null : new DocumentUploadFile((byte[])document.Data.Clone(), document.ContentType, document.FileName);
        progress?.Report(document?.Data.Length ?? 0);
        return Submit(++_submits);
    }

    public Task<ApiResult<VerificationAcceptedDto>> SubmitBankStatementAsync(SubmitBankStatementRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Do("submit-bank", request, document, idempotencyKey, progress);

    public Task<ApiResult<VerificationAcceptedDto>> SubmitPanAsync(SubmitPanRequest request, DocumentUploadFile? document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Do("submit-pan", request, document, idempotencyKey, progress);

    public Task<ApiResult<VerificationAcceptedDto>> SubmitPassportAsync(SubmitPassportRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default) =>
        Do("submit-passport", request, document, idempotencyKey, progress);

    public Task<ApiResult<VerificationStatusDto>> GetStatusAsync(Guid id, CancellationToken ct = default, bool passive = false)
    {
        StatusCalls.Add(("status", passive));
        return Status(StatusCalls.Count);
    }

    public Task<ApiResult<VerificationResultDto>> GetResultAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("result");
        return Result();
    }

    public Task<ApiResult<PagedResult<VerificationListItemDto>>> ListAsync(VerificationListQuery query, CancellationToken ct = default)
    {
        Calls.Add("list");
        LastQuery = query;
        return List(query);
    }

    public Task<ApiResult<PagedResult<BankTransactionDto>>> GetTransactionsAsync(Guid id, VerificationTransactionsQuery query, CancellationToken ct = default)
    {
        Calls.Add($"transactions:{query.Page}");
        return Transactions(query);
    }

    public Task<ApiResult<bool>> CancelAsync(Guid id, CancellationToken ct = default) { Calls.Add("cancel"); return Action(); }

    public Task<ApiResult<bool>> RetryExternalAsync(Guid id, CancellationToken ct = default) { Calls.Add("retry"); return Action(); }

    public Task<ApiResult<bool>> EraseAsync(Guid id, CancellationToken ct = default) { Calls.Add("erase"); return Action(); }
}

public sealed class FakeAdminVerificationApi : IAdminVerificationApiClient
{
    public List<string> Calls { get; } = [];

    public UpdateVerificationModuleRequest? LastUpdate { get; private set; }

    public string? LastCostReason { get; private set; }

    public (string Operation, int Credits)? LastCost { get; private set; }

    public VerificationStatsQuery? LastStatsQuery { get; private set; }

    public Guid? LastClient { get; private set; }

    public Func<Task<ApiResult<IReadOnlyList<AdminVerificationModuleDto>>>> Modules { get; set; } = () =>
        Ok.Of<IReadOnlyList<AdminVerificationModuleDto>>([VerificationSample.AdminModule("bank-statement"), VerificationSample.AdminModule(), VerificationSample.AdminUpcoming()]);

    public Func<Task<ApiResult<VerificationStatsDto>>> Stats { get; set; } = () => Ok.Of(VerificationSample.Stats());

    public Func<Task<ApiResult<bool>>> Update { get; set; } = () => Ok.Of(true);

    public Task<ApiResult<IReadOnlyList<AdminVerificationModuleDto>>> GetModulesAsync(CancellationToken ct = default) { Calls.Add("modules"); return Modules(); }

    public Task<ApiResult<bool>> UpdateModuleAsync(string key, UpdateVerificationModuleRequest request, CancellationToken ct = default)
    {
        Calls.Add("update:" + key);
        LastUpdate = request;
        return Update();
    }

    public Task<ApiResult<VerificationStatsDto>> GetStatsAsync(VerificationStatsQuery query, CancellationToken ct = default)
    {
        Calls.Add("stats");
        LastStatsQuery = query;
        return Stats();
    }

    public Task<ApiResult<PagedResult<AdminVerificationItemDto>>> ListRequestsAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken ct = default)
    {
        Calls.Add("list");
        LastClient = clientId;
        return Task.FromResult(Ok.Page(VerificationSample.AdminItem(clientId)));
    }

    public Task<ApiResult<AdminVerificationDetailDto>> GetRequestAsync(Guid clientId, Guid requestId, CancellationToken ct = default) =>
        Ok.Of(new AdminVerificationDetailDto(VerificationSample.AdminItem(clientId), [new VerificationEventDto(null, "Queued", null, null, "User", VerificationSample.Now), new VerificationEventDto("Queued", "Failed", null, "DOCUMENT_UNREADABLE", "System", VerificationSample.Now)]));

    public Task<ApiResult<PagedResult<AuditLogDto>>> GetAuditAsync(ActivityQuery query, CancellationToken ct = default) =>
        Task.FromResult(Ok.Page(new AuditLogDto(1, VerificationSample.Now, "verification.module_disabled", "VerificationModule", "pan", "User", null, null, "corr-a")));

    public Task<ApiResult<CostRuleDto>> SetDefaultCostAsync(string operation, int credits, string chargePolicy, string? reason, CancellationToken ct = default)
    {
        Calls.Add("cost:" + operation);
        LastCost = (operation, credits);
        LastCostReason = reason;
        return Ok.Of(new CostRuleDto(Guid.NewGuid(), "Platform", null, null, operation, credits, chargePolicy, VerificationSample.Now, null));
    }
}

public sealed class FakeDownloads : IVerificationDownloads
{
    public List<string> Calls { get; } = [];

    public Func<ApiResult<DownloadLinkDto>> Link { get; set; } = () => ApiResult<DownloadLinkDto>.Ok(new DownloadLinkDto("/api/v1/verifications/downloads/AAAAAAAAAAAAAAAAAAAA", DateTime.UtcNow.AddMinutes(1)));

    public Func<HttpResponseMessage> File { get; set; } = () => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html><script>alert(1)</script></html>", System.Text.Encoding.UTF8, "text/html") };

    public Task<ApiResult<DownloadLinkDto>> CreateLinkAsync(Guid requestId, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct)
    {
        Calls.Add($"link:{kind.Kind}:{kind.Format}:{options.SessionId}");
        return Task.FromResult(Link());
    }

    public Task<ApiResult<HttpResponseMessage>> OpenAsync(string downloadUrl, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct)
    {
        Calls.Add("open:" + downloadUrl);
        return Task.FromResult(ApiResult<HttpResponseMessage>.Ok(File()));
    }
}

public abstract class VerificationPageTestBase : ClientPageTestBase
{
    protected VerificationPageTestBase()
    {
        Api = new FakeVerificationApi();
        AdminApi = new FakeAdminVerificationApi();
        Services.AddSingleton<IVerificationApiClient>(Api);
        Services.AddSingleton<IAdminVerificationApiClient>(AdminApi);
    }

    protected FakeVerificationApi Api { get; }

    protected FakeAdminVerificationApi AdminApi { get; }

    protected void SignInAsVerifier(params string[] without) =>
        User.SignIn("Una Admin", "una@acme.test", "ClientAdmin", "Acme Corp", WebPermissions.ClientAdminDefaults.Where(p => !without.Contains(p)).ToArray());

    protected void SignInAsReader() =>
        User.SignIn("Rae Reader", "rae@acme.test", "ClientUser", "Acme Corp", [WebPermissions.VerificationRead]);

    protected static byte[] Pdf(int length = 4096)
    {
        var bytes = new byte[length];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        return bytes;
    }

    protected static byte[] Png(int length = 2048)
    {
        var bytes = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    protected static void Upload<T>(IRenderedComponent<T> cut, byte[] bytes, string name, string contentType = "application/pdf")
        where T : class, IComponent =>
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, name, null, contentType));

    protected static void ConfirmDialog(IRenderedComponent<MudDialogProvider> providers)
    {
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-ok]").Click();
    }
}
