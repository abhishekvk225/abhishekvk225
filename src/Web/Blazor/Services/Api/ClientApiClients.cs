using System.Globalization;
using System.Net.Http.Headers;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Web.Components;

namespace NexaVerify.Web.Services;

/// <summary>A client's own license picture (<c>/client/licenses</c>). The client always comes from the credential.</summary>
public interface IClientLicenseApiClient
{
    Task<ApiResult<LicenseSummaryDto>> GetSummaryAsync(CancellationToken ct = default);

    Task<ApiResult<LicenseDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken ct = default);
}

public sealed class ClientLicenseApiClient(IApiGateway api) : IClientLicenseApiClient
{
    public Task<ApiResult<LicenseSummaryDto>> GetSummaryAsync(CancellationToken ct = default) => api.GetAsync<LicenseSummaryDto>("client/licenses/summary", ct);

    public Task<ApiResult<LicenseDto>> GetAsync(Guid id, CancellationToken ct = default) => api.GetAsync<LicenseDto>($"client/licenses/{id}", ct);

    public Task<ApiResult<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<LicenseTransactionDto>>(ApiQuery.Paged($"client/licenses/{id}/transactions", page), ct);
}

/// <summary>Face recognition (<c>/faces/*</c>). Images travel as multipart uploads; nothing is logged or kept.</summary>
public interface IFacesApiClient
{
    Task<ApiResult<PagedResult<FaceProfileListItemDto>>> ListProfilesAsync(FaceProfileListQuery query, CancellationToken ct = default);

    Task<ApiResult<FaceProfileDto>> GetProfileAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<bool>> EraseProfileAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<bool>> EraseTemplateAsync(Guid profileId, Guid templateId, CancellationToken ct = default);

    Task<ApiResult<EnrollFaceResponse>> EnrollAsync(EnrollFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default);

    Task<ApiResult<VerifyFaceResponse>> VerifyAsync(VerifyFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default);

    Task<ApiResult<IdentifyFaceResponse>> IdentifyAsync(IdentifyFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default);

    Task<ApiResult<PagedResult<RecognitionRequestDto>>> GetHistoryAsync(RecognitionHistoryQuery query, CancellationToken ct = default);

    Task<ApiResult<RecognitionRequestDetailDto>> GetHistoryDetailAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<FaceBalanceDto>> GetBalanceAsync(CancellationToken ct = default);
}

public sealed class FacesApiClient(IApiGateway api) : IFacesApiClient
{
    public const string IdempotencyHeader = "Idempotency-Key";

    public Task<ApiResult<PagedResult<FaceProfileListItemDto>>> ListProfilesAsync(FaceProfileListQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<FaceProfileListItemDto>>(ApiQuery.With("faces/profiles", ("page", query.Page), ("pageSize", query.PageSize), ("search", query.Search), ("status", query.Status)), ct);

    public Task<ApiResult<FaceProfileDto>> GetProfileAsync(Guid id, CancellationToken ct = default) => api.GetAsync<FaceProfileDto>($"faces/profiles/{id}", ct);

    public Task<ApiResult<bool>> EraseProfileAsync(Guid id, CancellationToken ct = default) => api.SendAsync(HttpMethod.Delete, $"faces/profiles/{id}", null, ct);

    public Task<ApiResult<bool>> EraseTemplateAsync(Guid profileId, Guid templateId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"faces/profiles/{profileId}/templates/{templateId}", null, ct);

    public Task<ApiResult<EnrollFaceResponse>> EnrollAsync(EnrollFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default) =>
        UploadAsync<EnrollFaceResponse>("faces/enroll", image, idempotencyKey, ct,
            ("ExternalRef", request.ExternalRef), ("DisplayName", request.DisplayName), ("Metadata", request.Metadata), ("ConsentReference", request.ConsentReference));

    public Task<ApiResult<VerifyFaceResponse>> VerifyAsync(VerifyFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default) =>
        UploadAsync<VerifyFaceResponse>("faces/verify", image, idempotencyKey, ct,
            ("ProfileId", request.ProfileId?.ToString()), ("ExternalRef", request.ExternalRef));

    public Task<ApiResult<IdentifyFaceResponse>> IdentifyAsync(IdentifyFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default) =>
        UploadAsync<IdentifyFaceResponse>("faces/identify", image, idempotencyKey, ct, ("TopK", request.TopK?.ToString(CultureInfo.InvariantCulture)));

    public Task<ApiResult<PagedResult<RecognitionRequestDto>>> GetHistoryAsync(RecognitionHistoryQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<RecognitionRequestDto>>(
            ApiQuery.With("faces/requests", ("page", query.Page), ("pageSize", query.PageSize), ("operation", query.Operation), ("outcome", query.Outcome),
                ("profileId", query.ProfileId), ("from", query.From?.ToString("O", CultureInfo.InvariantCulture)), ("to", query.To?.ToString("O", CultureInfo.InvariantCulture))), ct);

    public Task<ApiResult<RecognitionRequestDetailDto>> GetHistoryDetailAsync(Guid id, CancellationToken ct = default) =>
        api.GetAsync<RecognitionRequestDetailDto>($"faces/requests/{id}", ct);

    public Task<ApiResult<FaceBalanceDto>> GetBalanceAsync(CancellationToken ct = default) => api.GetAsync<FaceBalanceDto>("faces/balance", ct);

    private Task<ApiResult<T>> UploadAsync<T>(string path, CapturedImage image, string idempotencyKey, CancellationToken ct, params (string Name, string? Value)[] fields)
    {
        // Built on demand (and again for a retry after a token refresh) around the caller's own buffer: the photo is never copied here.
        // The original file name is not sent (it may be a person's name); the API only needs the content type.
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

            var file = new ByteArrayContent(image.Data);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(image.ContentType);
            form.Add(file, "image", "upload" + ImageSniffer.ExtensionFor(image.ContentType));
            return form;
        }

        return api.SendAsync<T>(HttpMethod.Post, path, (Func<HttpContent>)BuildForm, ct,
            new ApiCallOptions { Headers = new Dictionary<string, string> { [IdempotencyHeader] = idempotencyKey } });
    }
}

/// <summary>API keys (<c>/client/api-keys</c>) and the request log (<c>/client/api-logs</c>).</summary>
public interface IApiKeysApiClient
{
    Task<ApiResult<IReadOnlyList<ApiKeyDto>>> ListAsync(CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<ApiScopeDto>>> ScopesAsync(CancellationToken ct = default);

    Task<ApiResult<CreatedApiKeyDto>> CreateAsync(CreateApiKeyRequest request, CancellationToken ct = default);

    Task<ApiResult<ApiKeyDto>> UpdateAsync(Guid id, UpdateApiKeyRequest request, CancellationToken ct = default);

    Task<ApiResult<ApiKeyDto>> RevokeAsync(Guid id, string reason, CancellationToken ct = default);

    Task<ApiResult<CreatedApiKeyDto>> RegenerateAsync(Guid id, int graceMinutes, CancellationToken ct = default);

    Task<ApiResult<PagedResult<ApiRequestLogDto>>> GetLogsAsync(ApiLogQuery query, CancellationToken ct = default);

    /// <summary>Per-key traffic from the client dashboard (last <paramref name="days"/> days).</summary>
    Task<ApiResult<IReadOnlyList<TopApiKeyDto>>> GetKeyUsageAsync(int days, CancellationToken ct = default);
}

public sealed class ApiKeysApiClient(IApiGateway api) : IApiKeysApiClient
{
    public Task<ApiResult<IReadOnlyList<ApiKeyDto>>> ListAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<ApiKeyDto>>("client/api-keys", ct);

    public Task<ApiResult<IReadOnlyList<ApiScopeDto>>> ScopesAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<ApiScopeDto>>("client/api-keys/scopes", ct);

    public Task<ApiResult<CreatedApiKeyDto>> CreateAsync(CreateApiKeyRequest request, CancellationToken ct = default) =>
        api.SendAsync<CreatedApiKeyDto>(HttpMethod.Post, "client/api-keys", request, ct);

    public Task<ApiResult<ApiKeyDto>> UpdateAsync(Guid id, UpdateApiKeyRequest request, CancellationToken ct = default) =>
        api.SendAsync<ApiKeyDto>(HttpMethod.Put, $"client/api-keys/{id}", request, ct);

    public Task<ApiResult<ApiKeyDto>> RevokeAsync(Guid id, string reason, CancellationToken ct = default) =>
        api.SendAsync<ApiKeyDto>(HttpMethod.Post, $"client/api-keys/{id}/revoke", new RevokeApiKeyRequest(reason), ct);

    public Task<ApiResult<CreatedApiKeyDto>> RegenerateAsync(Guid id, int graceMinutes, CancellationToken ct = default) =>
        api.SendAsync<CreatedApiKeyDto>(HttpMethod.Post, $"client/api-keys/{id}/regenerate", new RegenerateApiKeyRequest(graceMinutes), ct);

    public Task<ApiResult<PagedResult<ApiRequestLogDto>>> GetLogsAsync(ApiLogQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<ApiRequestLogDto>>(
            ApiQuery.With("client/api-logs", ("page", query.Page), ("pageSize", query.PageSize), ("apiKeyId", query.ApiKeyId), ("statusClass", query.StatusClass),
                ("from", query.From?.ToString("O", CultureInfo.InvariantCulture)), ("to", query.To?.ToString("O", CultureInfo.InvariantCulture))), ct);

    public async Task<ApiResult<IReadOnlyList<TopApiKeyDto>>> GetKeyUsageAsync(int days, CancellationToken ct = default)
    {
        var result = await api.GetAsync<ClientDashboardDto>($"client/dashboard?days={days}", ct);
        return result.IsSuccess ? ApiResult<IReadOnlyList<TopApiKeyDto>>.Ok(result.Value.TopApiKeys) : ApiResult<IReadOnlyList<TopApiKeyDto>>.Fail(result.Error!);
    }
}

/// <summary>Webhook endpoints and their delivery log (<c>/client/webhooks</c>).</summary>
public interface IWebhooksApiClient
{
    Task<ApiResult<IReadOnlyList<WebhookEndpointDto>>> ListAsync(CancellationToken ct = default);

    Task<ApiResult<WebhookEndpointDto>> GetAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<WebhookEventDto>>> EventsAsync(CancellationToken ct = default);

    Task<ApiResult<CreatedWebhookDto>> CreateAsync(CreateWebhookRequest request, CancellationToken ct = default);

    Task<ApiResult<WebhookEndpointDto>> UpdateAsync(Guid id, UpdateWebhookRequest request, CancellationToken ct = default);

    Task<ApiResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<CreatedWebhookDto>> RotateSecretAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<WebhookDeliveryDto>> SendTestAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<PagedResult<WebhookDeliveryDto>>> GetDeliveriesAsync(Guid id, PageRequest page, CancellationToken ct = default);

    Task<ApiResult<WebhookDeliveryDto>> RetryAsync(Guid id, long deliveryId, CancellationToken ct = default);
}

public sealed class WebhooksApiClient(IApiGateway api) : IWebhooksApiClient
{
    public Task<ApiResult<IReadOnlyList<WebhookEndpointDto>>> ListAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<WebhookEndpointDto>>("client/webhooks", ct);

    public Task<ApiResult<WebhookEndpointDto>> GetAsync(Guid id, CancellationToken ct = default) => api.GetAsync<WebhookEndpointDto>($"client/webhooks/{id}", ct);

    public Task<ApiResult<IReadOnlyList<WebhookEventDto>>> EventsAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<WebhookEventDto>>("client/webhooks/events", ct);

    public Task<ApiResult<CreatedWebhookDto>> CreateAsync(CreateWebhookRequest request, CancellationToken ct = default) =>
        api.SendAsync<CreatedWebhookDto>(HttpMethod.Post, "client/webhooks", request, ct);

    public Task<ApiResult<WebhookEndpointDto>> UpdateAsync(Guid id, UpdateWebhookRequest request, CancellationToken ct = default) =>
        api.SendAsync<WebhookEndpointDto>(HttpMethod.Put, $"client/webhooks/{id}", request, ct);

    public Task<ApiResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) => api.SendAsync(HttpMethod.Delete, $"client/webhooks/{id}", null, ct);

    public Task<ApiResult<CreatedWebhookDto>> RotateSecretAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<CreatedWebhookDto>(HttpMethod.Post, $"client/webhooks/{id}/rotate-secret", null, ct);

    public Task<ApiResult<WebhookDeliveryDto>> SendTestAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync<WebhookDeliveryDto>(HttpMethod.Post, $"client/webhooks/{id}/test", null, ct);

    public Task<ApiResult<PagedResult<WebhookDeliveryDto>>> GetDeliveriesAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<WebhookDeliveryDto>>(ApiQuery.Paged($"client/webhooks/{id}/deliveries", page), ct);

    public Task<ApiResult<WebhookDeliveryDto>> RetryAsync(Guid id, long deliveryId, CancellationToken ct = default) =>
        api.SendAsync<WebhookDeliveryDto>(HttpMethod.Post, $"client/webhooks/{id}/deliveries/{deliveryId}/retry", null, ct);
}

/// <summary>The client's own company profile, users, settings and activity (<c>/client/*</c>).</summary>
public interface IClientAccountApiClient
{
    Task<ApiResult<ClientDto>> GetProfileAsync(CancellationToken ct = default);

    Task<ApiResult<ClientDto>> UpdateProfileAsync(UpdateClientProfileRequest request, CancellationToken ct = default);

    Task<ApiResult<PagedResult<ClientUserDto>>> ListUsersAsync(PageRequest page, CancellationToken ct = default);

    Task<ApiResult<ClientUserDto>> CreateUserAsync(CreateClientUserRequest request, CancellationToken ct = default);

    Task<ApiResult<ClientUserDto>> UpdateUserAsync(Guid id, UpdateClientUserRequest request, CancellationToken ct = default);

    Task<ApiResult<bool>> ResetUserPasswordAsync(Guid id, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<SettingDto>>> GetSettingsAsync(CancellationToken ct = default);

    /// <param name="route">Settings area: recognition, notifications or security (see <see cref="SettingGroups"/>).</param>
    Task<ApiResult<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(string route, UpdateSettingsRequest request, CancellationToken ct = default);

    Task<ApiResult<PagedResult<AuditLogDto>>> GetActivityAsync(ActivityQuery query, CancellationToken ct = default);

    Task<ApiResult<PagedResult<LoginHistoryDto>>> GetLoginsAsync(ActivityQuery query, CancellationToken ct = default);
}

public sealed class ClientAccountApiClient(IApiGateway api) : IClientAccountApiClient
{
    public Task<ApiResult<ClientDto>> GetProfileAsync(CancellationToken ct = default) => api.GetAsync<ClientDto>("client/profile", ct);

    public Task<ApiResult<ClientDto>> UpdateProfileAsync(UpdateClientProfileRequest request, CancellationToken ct = default) =>
        api.SendAsync<ClientDto>(HttpMethod.Put, "client/profile", request, ct);

    public Task<ApiResult<PagedResult<ClientUserDto>>> ListUsersAsync(PageRequest page, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<ClientUserDto>>(ApiQuery.Paged("client/users", page), ct);

    public Task<ApiResult<ClientUserDto>> CreateUserAsync(CreateClientUserRequest request, CancellationToken ct = default) =>
        api.SendAsync<ClientUserDto>(HttpMethod.Post, "client/users", request, ct);

    public Task<ApiResult<ClientUserDto>> UpdateUserAsync(Guid id, UpdateClientUserRequest request, CancellationToken ct = default) =>
        api.SendAsync<ClientUserDto>(HttpMethod.Put, $"client/users/{id}", request, ct);

    public Task<ApiResult<bool>> ResetUserPasswordAsync(Guid id, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"client/users/{id}/reset-password", null, ct);

    public Task<ApiResult<IReadOnlyList<SettingDto>>> GetSettingsAsync(CancellationToken ct = default) => api.GetAsync<IReadOnlyList<SettingDto>>("client/settings", ct);

    public Task<ApiResult<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(string route, UpdateSettingsRequest request, CancellationToken ct = default) =>
        api.SendAsync<IReadOnlyList<SettingDto>>(HttpMethod.Put, $"client/settings/{route}", request, ct);

    public Task<ApiResult<PagedResult<AuditLogDto>>> GetActivityAsync(ActivityQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<AuditLogDto>>(Activity("client/audit-logs", query), ct);

    public Task<ApiResult<PagedResult<LoginHistoryDto>>> GetLoginsAsync(ActivityQuery query, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<LoginHistoryDto>>(Activity("client/logins", query), ct);

    private static string Activity(string path, ActivityQuery q) =>
        ApiQuery.With(path, ("page", q.Page), ("pageSize", q.PageSize), ("from", q.From?.ToString("O", CultureInfo.InvariantCulture)), ("to", q.To?.ToString("O", CultureInfo.InvariantCulture)),
            ("action", q.Action), ("outcome", q.Outcome));
}

/// <summary>In-app notification feed (<c>/client/notifications</c>).</summary>
public interface INotificationsApiClient
{
    /// <param name="background">True for polls the user did not ask for: they must not keep an idle session alive.</param>
    Task<ApiResult<NotificationFeedDto>> ListAsync(NotificationListQuery query, CancellationToken ct = default, bool background = false);

    Task<ApiResult<bool>> MarkReadAsync(Guid id, CancellationToken ct = default);
}

public sealed class NotificationsApiClient(IApiGateway api) : INotificationsApiClient
{
    public Task<ApiResult<NotificationFeedDto>> ListAsync(NotificationListQuery query, CancellationToken ct = default, bool background = false) =>
        api.GetAsync<NotificationFeedDto>(ApiQuery.With("client/notifications", ("page", query.Page), ("pageSize", query.PageSize), ("unreadOnly", query.UnreadOnly ? "true" : null)), ct,
            background ? new ApiCallOptions { Passive = true } : null);

    public Task<ApiResult<bool>> MarkReadAsync(Guid id, CancellationToken ct = default) => api.SendAsync(HttpMethod.Post, $"client/notifications/{id}/read", null, ct);
}

/// <summary>Which settings area (API route and permission) a setting group is edited through.</summary>
public static class SettingGroups
{
    public sealed record Area(string Route, string Permission);

    /// <summary>Groups the client may edit, as named in <c>SettingDto.Group</c>. Anything else is shown read-only.</summary>
    public static Area? For(string group) => group switch
    {
        "Recognition" => new Area("recognition", WebPermissions.SettingsRecognition),
        "Notifications" => new Area("notifications", WebPermissions.SettingsNotifications),
        "Integration" or "Security" => new Area("security", WebPermissions.SettingsSecurity),
        _ => null,
    };
}
