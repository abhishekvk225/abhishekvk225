using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Contracts.Verification;
using NexaVerify.Web.Components;

namespace NexaVerify.Web.Services;

/// <summary>
/// A client's own document checks (<c>/verifications/*</c>). The client always comes from the credential; documents travel as multipart
/// uploads and are never kept by the portal. Nothing here decides what a result means: that is the API's job.
/// </summary>
public interface IVerificationApiClient
{
    /// <summary>The catalogue: available, disabled and upcoming modules, with upload limits.</summary>
    Task<ApiResult<IReadOnlyList<VerificationModuleInfo>>> GetModulesAsync(CancellationToken ct = default);

    Task<ApiResult<VerificationAcceptedDto>> SubmitBankStatementAsync(SubmitBankStatementRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>The document is optional: a PAN can be checked from its number alone.</summary>
    Task<ApiResult<VerificationAcceptedDto>> SubmitPanAsync(SubmitPanRequest request, DocumentUploadFile? document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default);

    Task<ApiResult<VerificationAcceptedDto>> SubmitPassportAsync(SubmitPassportRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary><paramref name="passive"/>: a background poll that must not keep the sign-in session alive.</summary>
    Task<ApiResult<VerificationStatusDto>> GetStatusAsync(Guid id, CancellationToken ct = default, bool passive = false);

    Task<ApiResult<VerificationResultDto>> GetResultAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<PagedResult<VerificationListItemDto>>> ListAsync(VerificationListQuery query, CancellationToken ct = default);

    Task<ApiResult<PagedResult<BankTransactionDto>>> GetTransactionsAsync(Guid id, VerificationTransactionsQuery query, CancellationToken ct = default);

    Task<ApiResult<bool>> CancelAsync(Guid id, CancellationToken ct = default);

    /// <summary>Runs only the official (provider) step again. Answers 202; the caller then waits for the request to settle.</summary>
    Task<ApiResult<bool>> RetryExternalAsync(Guid id, CancellationToken ct = default);

    /// <summary>Immediately removes the document, the extracted data and the checks of one request.</summary>
    Task<ApiResult<bool>> EraseAsync(Guid id, CancellationToken ct = default);
}

public sealed class VerificationApiClient(IApiGateway api) : IVerificationApiClient
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public Task<ApiResult<IReadOnlyList<VerificationModuleInfo>>> GetModulesAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<VerificationModuleInfo>>("verifications/modules", ct);

    public Task<ApiResult<VerificationAcceptedDto>> SubmitBankStatementAsync(SubmitBankStatementRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default) =>
        UploadAsync("verifications/bank-statements", document, idempotencyKey, progress, ct,
            ("externalRef", request.ExternalRef), ("consentReference", request.ConsentReference), ("accountHolderName", request.AccountHolderName), ("bankName", request.BankName),
            ("accountNumberLast4", request.AccountNumberLast4), ("periodFrom", Date(request.PeriodFrom)), ("periodTo", Date(request.PeriodTo)));

    public Task<ApiResult<VerificationAcceptedDto>> SubmitPanAsync(SubmitPanRequest request, DocumentUploadFile? document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default) =>
        UploadAsync("verifications/pan", document, idempotencyKey, progress, ct,
            ("externalRef", request.ExternalRef), ("consentReference", request.ConsentReference), ("panNumber", request.PanNumber), ("fullName", request.FullName),
            ("dateOfBirth", Date(request.DateOfBirth)), ("fatherName", request.FatherName));

    public Task<ApiResult<VerificationAcceptedDto>> SubmitPassportAsync(SubmitPassportRequest request, DocumentUploadFile document, string idempotencyKey, IProgress<long>? progress = null, CancellationToken ct = default) =>
        UploadAsync("verifications/passports", document, idempotencyKey, progress, ct,
            ("externalRef", request.ExternalRef), ("consentReference", request.ConsentReference), ("passportNumber", request.PassportNumber), ("fullName", request.FullName),
            ("dateOfBirth", Date(request.DateOfBirth)), ("nationality", request.Nationality), ("dateOfIssue", Date(request.DateOfIssue)), ("dateOfExpiry", Date(request.DateOfExpiry)));

    public Task<ApiResult<VerificationStatusDto>> GetStatusAsync(Guid id, CancellationToken ct = default, bool passive = false) =>
        api.GetAsync<VerificationStatusDto>($"verifications/{id}/status", ct, passive ? new ApiCallOptions { Passive = true } : null);

    public Task<ApiResult<VerificationResultDto>> GetResultAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<VerificationResultDto>($"verifications/{id}", ct);

    public Task<ApiResult<PagedResult<VerificationListItemDto>>> ListAsync(VerificationListQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<VerificationListItemDto>>(
            ApiQuery.With("verifications", ("page", query.Page), ("pageSize", query.PageSize), ("sort", query.Sort), ("search", query.Search), ("type", query.Type), ("status", query.Status),
                ("reviewRequired", query.ReviewRequired is { } r ? (r ? "true" : "false") : null), ("externalState", query.ExternalState),
                ("from", query.From?.ToString("O", CultureInfo.InvariantCulture)), ("to", query.To?.ToString("O", CultureInfo.InvariantCulture))), ct);

    public Task<ApiResult<PagedResult<BankTransactionDto>>> GetTransactionsAsync(Guid id, VerificationTransactionsQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<BankTransactionDto>>(ApiQuery.With($"verifications/{id}/transactions", ("page", query.Page), ("pageSize", Math.Min(query.PageSize, VerificationTransactionsQuery.MaxPageSize))), ct);

    public Task<ApiResult<bool>> CancelAsync(Guid id, CancellationToken ct = default) => api.SendAsync(HttpMethod.Post, $"verifications/{id}/cancel", null, ct);

    public Task<ApiResult<bool>> RetryExternalAsync(Guid id, CancellationToken ct = default) => api.SendAsync(HttpMethod.Post, $"verifications/{id}/retry-external", null, ct);

    public Task<ApiResult<bool>> EraseAsync(Guid id, CancellationToken ct = default) => api.SendAsync(HttpMethod.Delete, $"verifications/{id}/data", null, ct);

    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Task<ApiResult<VerificationAcceptedDto>> UploadAsync(
        string path, DocumentUploadFile? document, string idempotencyKey, IProgress<long>? progress, CancellationToken ct, params (string Name, string? Value)[] fields)
    {
        // Built on demand (and again for a retry after a token refresh) around the caller's own buffer: the document is never copied here.
        // The original file name is not sent: it may be a person's name. The API only needs the bytes and the content type.
        HttpContent BuildForm()
        {
            var form = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    form.Add(new StringContent(value), name);
                }
            }

            if (document is not null)
            {
                form.Add(new ProgressByteContent(document.Data, document.ContentType, progress), "document", "upload" + DocumentSniffer.ExtensionFor(document.ContentType));
            }

            return form;
        }

        return api.SendAsync<VerificationAcceptedDto>(HttpMethod.Post, path, (Func<HttpContent>)BuildForm, ct,
            new ApiCallOptions { Headers = new Dictionary<string, string> { [IdempotencyHeader] = idempotencyKey } });
    }
}

/// <summary>Sends a byte buffer in chunks and reports how many bytes have gone out, so the page can show real upload progress.</summary>
internal sealed class ProgressByteContent : HttpContent
{
    private const int ChunkSize = 64 * 1024;
    private readonly byte[] _data;
    private readonly IProgress<long>? _progress;

    public ProgressByteContent(byte[] data, string contentType, IProgress<long>? progress)
    {
        _data = data;
        _progress = progress;
        Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => WriteAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = _data.Length;
        return true;
    }

    private async Task WriteAsync(Stream stream, CancellationToken ct)
    {
        long sent = 0;
        while (sent < _data.Length)
        {
            var count = (int)Math.Min(ChunkSize, _data.Length - sent);
            await stream.WriteAsync(_data.AsMemory((int)sent, count), ct);
            sent += count;
            _progress?.Report(sent);
        }
    }
}

/// <summary>
/// Download links for reports and exports. Used only by the portal's relay endpoints (they act for a session, not for a circuit), so
/// every call carries the session and address options. The API's link is never handed to the browser.
/// </summary>
public interface IVerificationDownloads
{
    Task<ApiResult<DownloadLinkDto>> CreateLinkAsync(Guid requestId, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct);

    /// <summary>Opens the file behind a link the API just issued, for streaming. The caller disposes the response.</summary>
    Task<ApiResult<HttpResponseMessage>> OpenAsync(string downloadUrl, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct);
}

public sealed partial class VerificationDownloads(IApiGateway api) : IVerificationDownloads
{
    public Task<ApiResult<DownloadLinkDto>> CreateLinkAsync(Guid requestId, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct) =>
        api.SendAsync<DownloadLinkDto>(HttpMethod.Post, $"verifications/{requestId}/downloads", new VerificationDownloadRequest(kind.Kind, kind.Format), ct, options);

    public Task<ApiResult<HttpResponseMessage>> OpenAsync(string downloadUrl, VerificationDownloadKind kind, ApiCallOptions options, CancellationToken ct)
    {
        // Only the path is used and it must be the API's own download route: whatever host or scheme the link names is ignored, so a
        // forged or poisoned link can never make the portal call somewhere else (SSRF) or carry the session to another site.
        if (!TryResolvePath(downloadUrl, out var path))
        {
            return Task.FromResult(ApiResult<HttpResponseMessage>.Fail("DOWNLOAD_LINK_INVALID", "The download could not be started. Please try again.", null, 502));
        }

        var accept = kind.ContentType is "text/html" or "text/csv" ? kind.ContentType : null;
        return api.OpenStreamAsync(path, ct, options with { Accept = accept });
    }

    /// <summary>Accepts <c>/api/v1/verifications/downloads/{token}</c> (absolute or relative) and returns the gateway-relative path.</summary>
    public static bool TryResolvePath(string? downloadUrl, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(downloadUrl) || downloadUrl.Length > 512)
        {
            return false;
        }

        var match = DownloadPath().Match(downloadUrl.Trim());
        if (!match.Success)
        {
            return false;
        }

        path = "verifications/downloads/" + match.Groups["token"].Value;
        return true;
    }

    [GeneratedRegex(@"^(?:https?://[A-Za-z0-9.\-:\[\]]+)?/api/v1/verifications/downloads/(?<token>[A-Za-z0-9_\-]{16,200})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex DownloadPath();
}

/// <summary>Platform staff: module status, statistics and metadata-only investigation (<c>/admin/verifications/*</c>).</summary>
public interface IAdminVerificationApiClient
{
    Task<ApiResult<IReadOnlyList<AdminVerificationModuleDto>>> GetModulesAsync(CancellationToken ct = default);

    /// <summary>Switch a module on or off. The reply body is not relied on: the page reloads the list (which also brings the new row version).</summary>
    Task<ApiResult<bool>> UpdateModuleAsync(string key, UpdateVerificationModuleRequest request, CancellationToken ct = default);

    Task<ApiResult<VerificationStatsDto>> GetStatsAsync(VerificationStatsQuery query, CancellationToken ct = default);

    Task<ApiResult<PagedResult<AdminVerificationItemDto>>> ListRequestsAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken ct = default);

    Task<ApiResult<AdminVerificationDetailDto>> GetRequestAsync(Guid clientId, Guid requestId, CancellationToken ct = default);

    Task<ApiResult<PagedResult<AuditLogDto>>> GetAuditAsync(ActivityQuery query, CancellationToken ct = default);

    /// <summary>
    /// Sets the platform default price of one operation through the existing cost-rule route. The optional <paramref name="reason"/> travels in
    /// the body as an extra property: the API ignores it until the request type gets a reason field (contract wish).
    /// </summary>
    Task<ApiResult<CostRuleDto>> SetDefaultCostAsync(string operation, int credits, string chargePolicy, string? reason, CancellationToken ct = default);
}

public sealed class AdminVerificationApiClient(IApiGateway api) : IAdminVerificationApiClient
{
    public Task<ApiResult<IReadOnlyList<AdminVerificationModuleDto>>> GetModulesAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<AdminVerificationModuleDto>>("admin/verifications/modules", ct);

    public Task<ApiResult<bool>> UpdateModuleAsync(string key, UpdateVerificationModuleRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"admin/verifications/modules/{Uri.EscapeDataString(key)}", request, ct);

    public Task<ApiResult<VerificationStatsDto>> GetStatsAsync(VerificationStatsQuery query, CancellationToken ct = default) =>
        api.GetAsync<VerificationStatsDto>(
            ApiQuery.With("admin/verifications/stats", ("from", query.From?.ToString("O", CultureInfo.InvariantCulture)), ("to", query.To?.ToString("O", CultureInfo.InvariantCulture)),
                ("type", query.Type), ("clientId", query.ClientId)), ct);

    public Task<ApiResult<PagedResult<AdminVerificationItemDto>>> ListRequestsAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<AdminVerificationItemDto>>(
            ApiQuery.With($"admin/clients/{clientId}/verifications", ("page", query.Page), ("pageSize", query.PageSize), ("type", query.Type), ("status", query.Status),
                ("from", query.From?.ToString("O", CultureInfo.InvariantCulture)), ("to", query.To?.ToString("O", CultureInfo.InvariantCulture))), ct);

    public Task<ApiResult<AdminVerificationDetailDto>> GetRequestAsync(Guid clientId, Guid requestId, CancellationToken ct = default) =>
        api.GetAsync<AdminVerificationDetailDto>($"admin/clients/{clientId}/verifications/{requestId}", ct);

    public Task<ApiResult<PagedResult<AuditLogDto>>> GetAuditAsync(ActivityQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<AuditLogDto>>(
            ApiQuery.With("admin/verifications/audit", ("page", query.Page), ("pageSize", query.PageSize), ("from", query.From?.ToString("O", CultureInfo.InvariantCulture)),
                ("to", query.To?.ToString("O", CultureInfo.InvariantCulture))), ct);

    public Task<ApiResult<CostRuleDto>> SetDefaultCostAsync(string operation, int credits, string chargePolicy, string? reason, CancellationToken ct = default) =>
        api.SendAsync<CostRuleDto>(HttpMethod.Put, "admin/cost-rules/default", new { operation, credits, chargePolicy, effectiveFrom = (DateTime?)null, reason }, ct);
}
