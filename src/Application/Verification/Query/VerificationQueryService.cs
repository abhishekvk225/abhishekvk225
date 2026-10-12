using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Query;

public interface IVerificationQueryService
{
    /// <summary>Available and Upcoming modules with effective status and limits. Upcoming entries carry no permission, limit or provider.</summary>
    Task<Result<IReadOnlyList<VerificationModuleDto>>> ListModulesAsync(CancellationToken cancellationToken);

    Task<Result<PagedResult<VerificationListItemDto>>> ListAsync(VerificationListQuery query, CancellationToken cancellationToken);

    /// <summary>Cheap polling target: no strict-table reads.</summary>
    Task<Result<VerificationStatusDto>> GetStatusAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<VerificationResultDto>> GetResultAsync(Guid id, CancellationToken cancellationToken);

    Task<Result<PagedResult<BankTransactionDto>>> GetTransactionsAsync(Guid id, VerificationTransactionsQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Read side for the calling client. Another tenant's id is "not found" (the repository reads are tenant-filtered). Credit figures are
/// included only when the caller also holds <c>license.read</c>.
/// </summary>
public sealed class VerificationQueryService : IVerificationQueryService
{
    public const int MaxPageSize = 100;

    private readonly ICurrentUser _user;
    private readonly IVerificationRepository _requests;
    private readonly IVerificationDocumentStore _documents;
    private readonly IVerificationResultStore _results;
    private readonly IVerificationModuleRegistry _registry;
    private readonly IVerificationProviderCatalog _providers;
    private readonly VerificationEnvelopeCodec _codec;
    private readonly IPermissionEvaluator _permissions;
    private readonly VerificationOptions _options;
    private readonly TimeProvider _time;

    public VerificationQueryService(
        ICurrentUser user,
        IVerificationRepository requests,
        IVerificationDocumentStore documents,
        IVerificationResultStore results,
        IVerificationModuleRegistry registry,
        IVerificationProviderCatalog providers,
        VerificationEnvelopeCodec codec,
        IPermissionEvaluator permissions,
        IOptions<VerificationOptions> options,
        TimeProvider time)
    {
        _user = user;
        _requests = requests;
        _documents = documents;
        _results = results;
        _registry = registry;
        _providers = providers;
        _codec = codec;
        _permissions = permissions;
        _options = options.Value;
        _time = time;
    }

    public async Task<Result<IReadOnlyList<VerificationModuleDto>>> ListModulesAsync(CancellationToken cancellationToken)
    {
        var modules = new List<VerificationModuleDto>();
        foreach (var d in _registry.All)
        {
            var status = await _registry.GetEffectiveStatusAsync(d.Key, cancellationToken).ConfigureAwait(false);
            if (d.Status == ModuleStatus.Upcoming)
            {
                modules.Add(new VerificationModuleDto(d.Key, d.DisplayName, d.Description, VerificationModuleStatuses.Upcoming, false, [], null, null, d.PlannedNote));
                continue;
            }

            var type = d.Type!.Value;
            var limits = new VerificationLimitsDto(
                (int)Math.Min(int.MaxValue, _options.Upload.MaxFileBytes), _options.Upload.AllowedExtensions, _options.Upload.MaxPages.For(type));
            modules.Add(new VerificationModuleDto(
                d.Key, d.DisplayName, d.Description, status.ToString(), status == ModuleStatus.Available, d.SubmitPermissions,
                _providers.HasProviderStage(type) ? _providers.IsConfigured(type) : null, limits, null));
        }

        return modules;
    }

    public async Task<Result<PagedResult<VerificationListItemDto>>> ListAsync(VerificationListQuery query, CancellationToken cancellationToken)
    {
        if (_user.ClientId is null)
        {
            return Error.Forbidden(ErrorCodes.Forbidden, "Document verification is only available to client accounts.");
        }

        var errors = new Dictionary<string, string[]>();
        VerificationType? type = null;
        VerificationStatus? status = null;
        ExternalState? external = null;
        var sort = VerificationListSort.CreatedDesc;
        if (query.Type is { Length: > 0 } t && !TryParse(t, out type))
        {
            errors["type"] = ["Unknown type."];
        }

        if (query.Status is { Length: > 0 } s && !TryParse(s, out status))
        {
            errors["status"] = ["Unknown status."];
        }

        if (query.ExternalState is { Length: > 0 } e && !TryParse(e, out external))
        {
            errors["externalState"] = ["Unknown provider state."];
        }

        if (query.Sort is { Length: > 0 } o && !Enum.TryParse(o, true, out sort))
        {
            errors["sort"] = ["Unknown sort."];
        }

        if (query.From is { } f && query.To is { } to && f > to)
        {
            errors["from"] = ["from must not be after to."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("The filter is not valid.", errors);
        }

        var page = Math.Clamp(query.Page, 1, PageRequest.MaxPage);
        var size = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var filter = new VerificationListFilter(type, status, query.ReviewRequired, external, query.From, query.To, query.Search?.Trim(), sort);
        var (items, total) = await _requests.ListAsync(filter, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);
        return new PagedResult<VerificationListItemDto>(items.Select(VerificationDtoMapper.ListItem).ToList(), page, size, total);
    }

    public async Task<Result<VerificationStatusDto>> GetStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await _requests.GetReadOnlyAsync(id, cancellationToken).ConfigureAwait(false);
        return request is null ? Error.NotFound("The verification was not found.") : VerificationDtoMapper.Status(request);
    }

    public async Task<Result<VerificationResultDto>> GetResultAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await _requests.GetReadOnlyAsync(id, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return Error.NotFound("The verification was not found.");
        }

        var document = await _documents.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var checks = await _results.ListChecksAsync(id, cancellationToken).ConfigureAwait(false);
        var data = await LoadDataAsync(request, cancellationToken).ConfigureAwait(false);
        var includeCredits = await _permissions.HasPermissionAsync(Permissions.LicenseView.Read, cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        return VerificationDtoMapper.Result(request, document, checks, data, _options, includeCredits, today);
    }

    public async Task<Result<PagedResult<BankTransactionDto>>> GetTransactionsAsync(Guid id, VerificationTransactionsQuery query, CancellationToken cancellationToken)
    {
        var request = await _requests.GetReadOnlyAsync(id, cancellationToken).ConfigureAwait(false);
        if (request is null || request.Type != VerificationType.BankStatement)
        {
            return Error.NotFound("The verification was not found.");
        }

        var data = await LoadDataAsync(request, cancellationToken).ConfigureAwait(false);
        if (data?.Extracted is not BankStatementData bank)
        {
            return Error.NotFound("No transactions are available for this verification.");
        }

        var page = Math.Clamp(query.Page, 1, PageRequest.MaxPage);
        var size = Math.Clamp(query.PageSize, 1, VerificationTransactionsQuery.MaxPageSize);
        var items = bank.Transactions.Skip((page - 1) * size).Take(size).Select(VerificationDtoMapper.Transaction).ToList();
        return new PagedResult<BankTransactionDto>(items, page, size, bank.Transactions.Count);
    }

    /// <summary>Opens the sealed result row; null when it does not exist (purged, erased, never created) or cannot be opened.</summary>
    internal async Task<SubmissionData?> LoadDataAsync(VerificationRequest request, CancellationToken cancellationToken)
    {
        if (request.PurgedAt is not null)
        {
            return null;
        }

        var row = await _results.GetResultAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var envelope = await _codec.OpenAsync(request.ClientId, request.Id, row.DataEnc, cancellationToken).ConfigureAwait(false);
        return envelope is null ? null : new SubmissionData(envelope.Extracted, ResultSupport.FromJson(row.FieldMatchesJson));
    }

    private static bool TryParse<T>(string value, out T? parsed)
        where T : struct, Enum
    {
        var ok = Enum.TryParse<T>(value, true, out var result) && Enum.IsDefined(result);
        parsed = ok ? result : null;
        return ok;
    }
}
