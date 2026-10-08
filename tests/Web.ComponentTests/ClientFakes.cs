using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Dashboards;
using NexaVerify.Contracts.Faces;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;

namespace NexaVerify.Web.ComponentTests;

public static class Ok
{
    public static Task<ApiResult<T>> Of<T>(T value) => Task.FromResult(ApiResult<T>.Ok(value));

    public static Task<ApiResult<T>> Fail<T>(string code = "INTERNAL_ERROR", string message = "Something went wrong on our side. Please try again.", string? correlation = "corr-x", int status = 500) =>
        Task.FromResult(ApiResult<T>.Fail(code, message, correlation, status));

    public static ApiResult<PagedResult<T>> Page<T>(params T[] items) => ApiResult<PagedResult<T>>.Ok(new PagedResult<T>(items, 1, 25, items.Length));
}

public sealed class FakeClientLicenseApi : IClientLicenseApiClient
{
    public Func<Task<ApiResult<LicenseSummaryDto>>> Summary { get; set; } = () => Ok.Of(ClientSample.Summary());

    public Func<Task<ApiResult<LicenseDto>>> License { get; set; } = () => Ok.Of(Sample.License());

    public Task<ApiResult<LicenseSummaryDto>> GetSummaryAsync(CancellationToken ct = default) => Summary();

    public Task<ApiResult<LicenseDto>> GetAsync(Guid id, CancellationToken ct = default) => License();

    public Task<ApiResult<PagedResult<LicenseTransactionDto>>> GetTransactionsAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        Task.FromResult(Ok.Page(new LicenseTransactionDto(7, id, "Consume", -2, 100, 98, "Verify", null, null, null, "ApiKey", null, Sample.Now)));
}

public sealed class FakeFacesApi : IFacesApiClient
{
    public List<string> Calls { get; } = [];

    /// <summary>Copies of the image bytes as they were when each upload call arrived.</summary>
    public List<byte[]> UploadedImages { get; } = [];

    public List<string> IdempotencyKeys { get; } = [];

    public List<byte[]> LiveBuffers { get; } = [];

    public Func<FaceProfileListQuery, Task<ApiResult<PagedResult<FaceProfileListItemDto>>>> Profiles { get; set; } = _ => Task.FromResult(Ok.Page(ClientSample.ProfileItem()));

    public Func<Task<ApiResult<FaceProfileDto>>> Profile { get; set; } = () => Ok.Of(ClientSample.Profile());

    public Func<Task<ApiResult<EnrollFaceResponse>>> Enroll { get; set; } = () => Ok.Of(new EnrollFaceResponse(Guid.NewGuid(), Guid.NewGuid(), true, 0.93m, "Enrolled", Guid.NewGuid(), new CreditsDto(1, 99)));

    public Func<Task<ApiResult<VerifyFaceResponse>>> Verify { get; set; } = () => Ok.Of(new VerifyFaceResponse(true, 0.912m, 0.6m, "Matched", Guid.NewGuid(), Guid.NewGuid(), new CreditsDto(1, 98)));

    public Func<Task<ApiResult<IdentifyFaceResponse>>> Identify { get; set; } = () =>
        Ok.Of(new IdentifyFaceResponse([new FaceMatchDto(Guid.NewGuid(), "EMP-1001", 0.88m)], 0.88m, 0.6m, "Matched", Guid.NewGuid(), new CreditsDto(2, 96)));

    public Func<RecognitionHistoryQuery, Task<ApiResult<PagedResult<RecognitionRequestDto>>>> History { get; set; } = _ => Task.FromResult(Ok.Page(ClientSample.Request()));

    public Task<ApiResult<PagedResult<FaceProfileListItemDto>>> ListProfilesAsync(FaceProfileListQuery query, CancellationToken ct = default)
    {
        Calls.Add($"profiles:{query.Search}:{query.Status}:{query.Page}");
        return Profiles(query);
    }

    public Task<ApiResult<FaceProfileDto>> GetProfileAsync(Guid id, CancellationToken ct = default) => Profile();

    public Task<ApiResult<bool>> EraseProfileAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add($"erase:{id}");
        return Ok.Of(true);
    }

    public Task<ApiResult<bool>> EraseTemplateAsync(Guid profileId, Guid templateId, CancellationToken ct = default)
    {
        Calls.Add($"erase-template:{templateId}");
        return Ok.Of(true);
    }

    public Task<ApiResult<EnrollFaceResponse>> EnrollAsync(EnrollFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default)
    {
        Record($"enroll:{request.ExternalRef}:{request.ConsentReference}", image, idempotencyKey);
        return Enroll();
    }

    public Task<ApiResult<VerifyFaceResponse>> VerifyAsync(VerifyFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default)
    {
        Record($"verify:{request.ExternalRef}", image, idempotencyKey);
        return Verify();
    }

    public Task<ApiResult<IdentifyFaceResponse>> IdentifyAsync(IdentifyFaceRequest request, CapturedImage image, string idempotencyKey, CancellationToken ct = default)
    {
        Record($"identify:{request.TopK}", image, idempotencyKey);
        return Identify();
    }

    public Task<ApiResult<PagedResult<RecognitionRequestDto>>> GetHistoryAsync(RecognitionHistoryQuery query, CancellationToken ct = default)
    {
        Calls.Add($"history:{query.Operation}:{query.Outcome}");
        return History(query);
    }

    public Task<ApiResult<RecognitionRequestDetailDto>> GetHistoryDetailAsync(Guid id, CancellationToken ct = default) =>
        Ok.Of(new RecognitionRequestDetailDto(ClientSample.Request(), 90, [new RecognitionCandidateDto(1, Guid.NewGuid(), "EMP-1001", 0.9m, true)]));

    public Task<ApiResult<FaceBalanceDto>> GetBalanceAsync(CancellationToken ct = default) => Ok.Of(new FaceBalanceDto(100, null, "Active"));

    private void Record(string call, CapturedImage image, string key)
    {
        Calls.Add(call);
        UploadedImages.Add((byte[])image.Data.Clone());
        LiveBuffers.Add(image.Data);
        IdempotencyKeys.Add(key);
    }
}

public sealed class FakeApiKeysApi : IApiKeysApiClient
{
    public List<string> Calls { get; } = [];

    public Func<Task<ApiResult<IReadOnlyList<ApiKeyDto>>>> Keys { get; set; } = () => Ok.Of<IReadOnlyList<ApiKeyDto>>([ClientSample.Key()]);

    public Func<Task<ApiResult<CreatedApiKeyDto>>> Created { get; set; } = () => Ok.Of(new CreatedApiKeyDto(ClientSample.Key(), ClientSample.RawKey));

    public Task<ApiResult<IReadOnlyList<ApiKeyDto>>> ListAsync(CancellationToken ct = default) => Keys();

    public Task<ApiResult<IReadOnlyList<ApiScopeDto>>> ScopesAsync(CancellationToken ct = default) =>
        Ok.Of<IReadOnlyList<ApiScopeDto>>([new ApiScopeDto("faces.verify", "Verify a face (1:1)"), new ApiScopeDto("faces.identify", "Identify a face (1:N)"), new ApiScopeDto("billing.read", "Not a face scope")]);

    public Task<ApiResult<CreatedApiKeyDto>> CreateAsync(CreateApiKeyRequest request, CancellationToken ct = default)
    {
        Calls.Add($"create:{request.Name}:{string.Join('+', request.Scopes)}:{request.RateLimitPerMinute}:{string.Join('+', request.AllowedIps ?? [])}");
        return Created();
    }

    public Task<ApiResult<ApiKeyDto>> UpdateAsync(Guid id, UpdateApiKeyRequest request, CancellationToken ct = default)
    {
        Calls.Add($"update:{request.Name}");
        return Ok.Of(ClientSample.Key());
    }

    public Task<ApiResult<ApiKeyDto>> RevokeAsync(Guid id, string reason, CancellationToken ct = default)
    {
        Calls.Add($"revoke:{reason}");
        return Ok.Of(ClientSample.Key() with { Status = "Revoked", EffectiveStatus = "Revoked" });
    }

    public Task<ApiResult<CreatedApiKeyDto>> RegenerateAsync(Guid id, int graceMinutes, CancellationToken ct = default)
    {
        Calls.Add($"regenerate:{graceMinutes}");
        return Ok.Of(new CreatedApiKeyDto(ClientSample.Key(), ClientSample.RawKey2));
    }

    public Task<ApiResult<PagedResult<ApiRequestLogDto>>> GetLogsAsync(ApiLogQuery query, CancellationToken ct = default)
    {
        Calls.Add($"logs:{query.StatusClass}:{query.ApiKeyId}");
        return Task.FromResult(Ok.Page(new ApiRequestLogDto(1, ClientSample.Key().Id, "POST", "/api/v1/faces/verify", 200, 42, "203.0.113.7", null, "corr-1", Sample.Now)));
    }

    public Task<ApiResult<IReadOnlyList<TopApiKeyDto>>> GetKeyUsageAsync(int days, CancellationToken ct = default) =>
        Ok.Of<IReadOnlyList<TopApiKeyDto>>([new TopApiKeyDto(ClientSample.Key().Id, "Front desk", "nv_live_ab", 1234, 5, Sample.Now)]);
}

public sealed class FakeWebhooksApi : IWebhooksApiClient
{
    public List<string> Calls { get; } = [];

    public Func<Task<ApiResult<IReadOnlyList<WebhookEndpointDto>>>> Endpoints { get; set; } = () => Ok.Of<IReadOnlyList<WebhookEndpointDto>>([ClientSample.Hook()]);

    public Func<IReadOnlyList<WebhookDeliveryDto>> Deliveries { get; set; } = () =>
    [
        new WebhookDeliveryDto(11, Guid.NewGuid(), "recognition.completed", "Delivered", 1, Sample.Now, 200, null, Sample.Now, Sample.Now),
        new WebhookDeliveryDto(12, Guid.NewGuid(), "license.low_balance", "Abandoned", 6, Sample.Now, 500, "<script>alert(1)</script>", Sample.Now, null),
    ];

    public Task<ApiResult<IReadOnlyList<WebhookEndpointDto>>> ListAsync(CancellationToken ct = default) => Endpoints();

    public Task<ApiResult<WebhookEndpointDto>> GetAsync(Guid id, CancellationToken ct = default) => Ok.Of(ClientSample.Hook());

    public Task<ApiResult<IReadOnlyList<WebhookEventDto>>> EventsAsync(CancellationToken ct = default) =>
        Ok.Of<IReadOnlyList<WebhookEventDto>>([new WebhookEventDto("recognition.completed", "A check finished."), new WebhookEventDto("license.low_balance", "Credits are low.")]);

    public Task<ApiResult<CreatedWebhookDto>> CreateAsync(CreateWebhookRequest request, CancellationToken ct = default)
    {
        Calls.Add($"create:{request.Name}:{request.Url}:{string.Join('+', request.Events)}");
        return Ok.Of(new CreatedWebhookDto(ClientSample.Hook(), ClientSample.WebhookSecret));
    }

    public Task<ApiResult<WebhookEndpointDto>> UpdateAsync(Guid id, UpdateWebhookRequest request, CancellationToken ct = default)
    {
        Calls.Add($"update:{request.Enabled}");
        return Ok.Of(ClientSample.Hook());
    }

    public Task<ApiResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("delete");
        return Ok.Of(true);
    }

    public Task<ApiResult<CreatedWebhookDto>> RotateSecretAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("rotate");
        return Ok.Of(new CreatedWebhookDto(ClientSample.Hook(), ClientSample.WebhookSecret));
    }

    public Task<ApiResult<WebhookDeliveryDto>> SendTestAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("test");
        return Ok.Of(Deliveries()[0]);
    }

    public Task<ApiResult<PagedResult<WebhookDeliveryDto>>> GetDeliveriesAsync(Guid id, PageRequest page, CancellationToken ct = default) =>
        Task.FromResult(Ok.Page(Deliveries().ToArray()));

    public Task<ApiResult<WebhookDeliveryDto>> RetryAsync(Guid id, long deliveryId, CancellationToken ct = default)
    {
        Calls.Add($"retry:{deliveryId}");
        return Ok.Of(Deliveries()[1]);
    }
}

public sealed class FakeAccountApi : IClientAccountApiClient
{
    public List<string> Calls { get; } = [];

    public Func<Task<ApiResult<PagedResult<ClientUserDto>>>> Users { get; set; } = () => Task.FromResult(Ok.Page(ClientSample.User()));

    public Func<Task<ApiResult<IReadOnlyList<SettingDto>>>> Settings { get; set; } = () => Ok.Of(ClientSample.Settings());

    public Task<ApiResult<ClientDto>> GetProfileAsync(CancellationToken ct = default) => Ok.Of(Sample.Client(Guid.NewGuid()));

    public Task<ApiResult<ClientDto>> UpdateProfileAsync(UpdateClientProfileRequest request, CancellationToken ct = default)
    {
        Calls.Add($"profile:{request.Name}");
        return Ok.Of(Sample.Client(Guid.NewGuid()) with { Name = request.Name });
    }

    public Task<ApiResult<PagedResult<ClientUserDto>>> ListUsersAsync(PageRequest page, CancellationToken ct = default) => Users();

    public Task<ApiResult<ClientUserDto>> CreateUserAsync(CreateClientUserRequest request, CancellationToken ct = default)
    {
        Calls.Add($"invite:{request.Email}:{request.Role}");
        return Ok.Of(ClientSample.User());
    }

    public Task<ApiResult<ClientUserDto>> UpdateUserAsync(Guid id, UpdateClientUserRequest request, CancellationToken ct = default)
    {
        Calls.Add($"update-user:{request.Role}:{request.IsActive}");
        return Ok.Of(ClientSample.User());
    }

    public Task<ApiResult<bool>> ResetUserPasswordAsync(Guid id, CancellationToken ct = default)
    {
        Calls.Add("reset-user");
        return Ok.Of(true);
    }

    public Task<ApiResult<IReadOnlyList<SettingDto>>> GetSettingsAsync(CancellationToken ct = default) => Settings();

    public Task<ApiResult<IReadOnlyList<SettingDto>>> UpdateSettingsAsync(string route, UpdateSettingsRequest request, CancellationToken ct = default)
    {
        Calls.Add($"settings:{route}:{string.Join(';', request.Values.Select(v => $"{v.Key}={v.Value.GetRawText()}"))}");
        return Settings();
    }

    public Task<ApiResult<PagedResult<AuditLogDto>>> GetActivityAsync(ActivityQuery query, CancellationToken ct = default) =>
        Task.FromResult(Ok.Page(new AuditLogDto(1, Sample.Now, "apikey.created", "ApiKey", "1", "User", null, "203.0.113.7", "corr")));

    public Task<ApiResult<PagedResult<LoginHistoryDto>>> GetLoginsAsync(ActivityQuery query, CancellationToken ct = default) =>
        Task.FromResult(Ok.Page(new LoginHistoryDto(1, Sample.Now, "Success", "una@acme.test", null, "203.0.113.7", "Firefox", null)));
}

public sealed class FakeNotificationsApi : INotificationsApiClient
{
    private int _calls;

    public int Calls => _calls;

    private int _backgroundCalls;

    /// <summary>How many calls were flagged as background polls (they must not count as user activity).</summary>
    public int BackgroundCalls => _backgroundCalls;

    public List<Guid> MarkedRead { get; } = [];

    public long Unread { get; set; } = 3;

    public Func<NotificationListQuery, Task<ApiResult<NotificationFeedDto>>>? Override { get; set; }

    public Task<ApiResult<NotificationFeedDto>> ListAsync(NotificationListQuery query, CancellationToken ct = default, bool background = false)
    {
        Interlocked.Increment(ref _calls);
        if (background)
        {
            Interlocked.Increment(ref _backgroundCalls);
        }

        if (Override is not null)
        {
            return Override(query);
        }

        var items = new[]
        {
            new NotificationDto(Guid.Parse("00000000-0000-0000-0000-000000000001"), "license.low_balance", "Warning", "Credits are running low", "<b>20%</b> left", Sample.Now, false),
            new NotificationDto(Guid.Parse("00000000-0000-0000-0000-000000000002"), "license.expiring", "Info", "License ends soon", "In 30 days", Sample.Now, true),
        };
        return Ok.Of(new NotificationFeedDto(Unread, new PagedResult<NotificationDto>(items, query.Page, query.PageSize, items.Length)));
    }

    public Task<ApiResult<bool>> MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        MarkedRead.Add(id);
        Unread = Math.Max(0, Unread - 1);
        return Ok.Of(true);
    }
}

public static class ClientSample
{
    public const string RawKey = "nv_live_RAWKEY_ONLY_SHOWN_ONCE_123456";
    public const string RawKey2 = "nv_live_REPLACEMENT_KEY_ONCE_654321";
    public const string WebhookSecret = "whsec_SIGNING_SECRET_ONCE_abcdef";

    public static LicenseSummaryDto Summary() =>
        new("Healthy", "842 credits available.", 842, 1000, 158, 84, Sample.Now.AddDays(23), 23, 1, [Sample.LicenseItem()]);

    public static FaceProfileListItemDto ProfileItem() => new(Guid.NewGuid(), "EMP-1001", "Ada Lovelace", "Active", 2, Sample.Now.AddDays(300), Sample.Now);

    public static FaceProfileDto Profile() => new(
        Guid.NewGuid(), "EMP-1001", "Ada Lovelace", "{\"note\":\"<b>x</b>\"}", "Active", "FORM-42", Sample.Now, null, 2,
        [new FaceTemplateDto(Guid.NewGuid(), "mock", "v1", 0.93m, "Active", Sample.Now), new FaceTemplateDto(Guid.NewGuid(), "mock", "v1", 0.88m, "Active", Sample.Now)], Sample.Now, null);

    public static RecognitionRequestDto Request() =>
        new(Guid.NewGuid(), "Verify", "Portal", "Completed", "Matched", null, 0.6m, 0.91m, 1, "mock", 42, 1, null, Sample.Now);

    public static ApiKeyDto Key() => new(
        Guid.Parse("00000000-0000-0000-0000-0000000000aa"), "Front desk", "nv_live_ab", ["faces.verify"], "Active", "Active", null, null, null, 60, [], null, Sample.Now, null, null, "AAAA");

    public static WebhookEndpointDto Hook() => new(
        Guid.Parse("00000000-0000-0000-0000-0000000000bb"), "Back office", "https://hooks.acme.test/nv", ["recognition.completed"], "Active", 0, null, null, Sample.Now, "BBBB");

    public static ClientUserDto User() =>
        new(Guid.Parse("00000000-0000-0000-0000-0000000000cc"), "una@acme.test", "Una <i>User</i>", "Active", "ClientUser", "Clerk", false, Sample.Now, false, Sample.Now);

    public static IReadOnlyList<SettingDto> Settings() =>
    [
        Setting("notify.lowBalancePercent", "Notifications", "Int", "20", "Warn when remaining credits fall below this percentage.", true, 1, 90),
        Setting("notify.emailEnabled", "Notifications", "Bool", "true", "Send alert emails.", true),
        Setting("integration.allowedIps", "Integration", "StringList", "[\"203.0.113.7\"]", "IP addresses allowed to use the API keys.", true),
        Setting("api.rateLimitPerMinute", "API", "Int", "60", "API requests allowed per minute per key.", false, 1, 100000),
    ];

    private static SettingDto Setting(string key, string group, string type, string json, string description, bool editable, decimal? min = null, decimal? max = null)
    {
        var value = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        return new SettingDto(key, group, type, value, value, min, max, editable ? "Client" : "Platform", editable, false, description);
    }
}
