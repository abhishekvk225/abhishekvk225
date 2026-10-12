using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Query;

public interface IVerificationDownloadService
{
    /// <summary>Creates a short-lived, single-use, principal-bound grant and returns its relative URL.</summary>
    Task<Result<DownloadLinkDto>> CreateLinkAsync(Guid id, VerificationDownloadRequest request, CancellationToken cancellationToken);

    /// <summary>Consumes the grant (one conditional UPDATE) and renders the file. Requires the same authentication and permission as creating it.</summary>
    Task<Result<DownloadContent>> RedeemAsync(string token, CancellationToken cancellationToken);
}

public sealed class VerificationDownloadService : IVerificationDownloadService
{
    public const string RoutePrefix = "/api/v1/verifications/downloads/";

    private readonly ICurrentUser _user;
    private readonly IVerificationRepository _requests;
    private readonly IVerificationDownloadGrants _grants;
    private readonly IDocumentStorageService _storage;
    private readonly IVerificationDocumentStore _documents;
    private readonly IVerificationQueryService _query;
    private readonly ISecureTokenService _tokens;
    private readonly IPermissionEvaluator _permissions;
    private readonly IAuditService _audit;
    private readonly IUnitOfWork _unitOfWork;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _time;

    public VerificationDownloadService(
        ICurrentUser user,
        IVerificationRepository requests,
        IVerificationDownloadGrants grants,
        IDocumentStorageService storage,
        IVerificationDocumentStore documents,
        IVerificationQueryService query,
        ISecureTokenService tokens,
        IPermissionEvaluator permissions,
        IAuditService audit,
        IUnitOfWork unitOfWork,
        IOptions<VerificationOptions> options,
        TimeProvider time)
    {
        _user = user;
        _requests = requests;
        _grants = grants;
        _storage = storage;
        _documents = documents;
        _query = query;
        _tokens = tokens;
        _permissions = permissions;
        _audit = audit;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<Result<DownloadLinkDto>> CreateLinkAsync(Guid id, VerificationDownloadRequest request, CancellationToken cancellationToken)
    {
        if (_user.ClientId is not { } clientId)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Downloads are only available to client accounts.");
        }

        var principal = _user.ActorId ?? clientId;

        if (!Enum.TryParse<DownloadKind>(request.Kind, true, out var kind) || !Enum.IsDefined(kind))
        {
            return Error.Validation("Unknown download kind.");
        }

        var verification = await _requests.GetReadOnlyAsync(id, cancellationToken).ConfigureAwait(false);
        if (verification is null)
        {
            return Error.NotFound("The verification was not found.");
        }

        var availability = await CheckAvailableAsync(verification, kind, cancellationToken).ConfigureAwait(false);
        if (availability is not null)
        {
            return availability;
        }

        var token = _tokens.CreateToken();
        var now = Now;
        _grants.Add(VerificationDownloadGrant.Create(
            clientId, id, kind, kind == DownloadKind.Report ? (request.Format ?? VerificationDownloadKinds.Json).ToLowerInvariant() : null, principal,
            _tokens.Hash(token), now, TimeSpan.FromSeconds(_options.Download.LinkSeconds)));
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DownloadLinkDto(RoutePrefix + token, now.AddSeconds(_options.Download.LinkSeconds));
    }

    public async Task<Result<DownloadContent>> RedeemAsync(string token, CancellationToken cancellationToken)
    {
        if (_user.ClientId is not { } clientId || string.IsNullOrWhiteSpace(token))
        {
            return Error.NotFound("The download link was not found.");
        }

        var principal = _user.ActorId ?? clientId;

        var result = await _grants.ConsumeAsync(_tokens.Hash(token), principal, Now, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case GrantConsumeStatus.ExpiredOrUsed:
                return Error.Gone(ErrorCodes.DownloadLinkExpired, "This download link has expired or was already used.");
            case GrantConsumeStatus.Consumed when result is { RequestId: { } requestId, Kind: { } kind }:
                return await RenderAsync(requestId, kind, result.Format, cancellationToken).ConfigureAwait(false);
            default:
                return Error.NotFound("The download link was not found."); // unknown token, another tenant's token, another principal's token
        }
    }

    private async Task<Error?> CheckAvailableAsync(VerificationRequest verification, DownloadKind kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case DownloadKind.Report:
                return verification.IsTerminal ? null : Error.Conflict(ErrorCodes.Conflict, "The report is available once processing has finished.");
            case DownloadKind.TransactionsCsv:
                return verification.Type == VerificationType.BankStatement && verification.IsTerminal && verification.PurgedAt is null
                    ? null
                    : Error.NotFound("No transactions are available for this verification.");
            default:
                if (!_options.Download.AllowOriginalDownload)
                {
                    return Error.NotFound("Downloading the original document is not enabled.");
                }

                if (!await _permissions.HasPermissionAsync(Permissions.Verification.DocumentDownload, cancellationToken).ConfigureAwait(false))
                {
                    return Error.Forbidden(ErrorCodes.Forbidden, "You do not have permission to download the original document.");
                }

                return verification.PurgedAt is null && verification.UploadState == StageState.Completed
                    ? null
                    : Error.NotFound("The original document is no longer available.");
        }
    }

    private async Task<Result<DownloadContent>> RenderAsync(Guid requestId, DownloadKind kind, string? format, CancellationToken cancellationToken)
    {
        if (kind == DownloadKind.OriginalDocument)
        {
            // Re-checked at redemption: the option and the permission may have changed since the link was issued.
            if (!_options.Download.AllowOriginalDownload
                || !await _permissions.HasPermissionAsync(Permissions.Verification.DocumentDownload, cancellationToken).ConfigureAwait(false))
            {
                return Error.Forbidden(ErrorCodes.Forbidden, "You do not have permission to download the original document.");
            }

            var document = await _documents.GetAsync(requestId, cancellationToken).ConfigureAwait(false);
            var bytes = document is null ? null : await _storage.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
            if (document is null || bytes is null || bytes.IsFailure)
            {
                return Error.NotFound("The original document is no longer available.");
            }

            Audit(VerificationAuditActions.DocumentDownloaded, requestId);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new DownloadContent(bytes.Value, document.ContentType, $"verification-{requestId:N}{document.Extension}");
        }

        if (!await _permissions.HasPermissionAsync(Permissions.Verification.ReportDownload, cancellationToken).ConfigureAwait(false))
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "You do not have permission to download reports.");
        }

        var result = await _query.GetResultAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        DownloadContent content;
        if (kind == DownloadKind.TransactionsCsv)
        {
            var page = await _query.GetTransactionsAsync(
                requestId, new VerificationTransactionsQuery { Page = 1, PageSize = VerificationTransactionsQuery.MaxPageSize }, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure)
            {
                return page.Error!;
            }

            var all = new List<BankTransactionDto>(page.Value.Items);
            for (var next = 2; all.Count < page.Value.TotalCount; next++)
            {
                var more = await _query.GetTransactionsAsync(
                    requestId, new VerificationTransactionsQuery { Page = next, PageSize = VerificationTransactionsQuery.MaxPageSize }, cancellationToken).ConfigureAwait(false);
                if (more.IsFailure || more.Value.Items.Count == 0)
                {
                    break;
                }

                all.AddRange(more.Value.Items);
            }

            content = VerificationReports.TransactionsCsv(requestId, all);
        }
        else
        {
            content = string.Equals(format, VerificationDownloadKinds.Html, StringComparison.OrdinalIgnoreCase)
                ? VerificationReports.Html(result.Value, Now)
                : VerificationReports.Json(result.Value, Now);
        }

        Audit(VerificationAuditActions.ReportDownloaded, requestId);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return content;
    }

    private void Audit(string action, Guid requestId) =>
        _audit.Record(new AuditEntry(action, VerificationAuditActions.EntityType, requestId.ToString(), _user.ClientId, NewValues: new { kind = action }));
}
