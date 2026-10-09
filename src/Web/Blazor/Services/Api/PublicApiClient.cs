using Microsoft.Extensions.Caching.Memory;

namespace NexaVerify.Web.Services;

/// <summary>A plan as the public website shows it (<c>GET /api/v1/public/plans</c>). No price text means "Contact us".</summary>
public sealed record PublicPlanDto(
    Guid Id,
    string Name,
    string? Description,
    int Credits,
    int ValidityDays,
    IReadOnlyList<string> Highlights,
    bool IsTrial,
    string? DisplayPrice = null);

public sealed record CaptchaConfigDto(string Provider, string? SiteKey)
{
    public const string TurnstileProvider = "turnstile";

    public bool IsTurnstile => string.Equals(Provider, TurnstileProvider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(SiteKey);
}

/// <summary>Public sign-up settings (<c>GET /api/v1/public/config</c>).</summary>
public sealed record PublicConfigDto(bool SignupEnabled, int TrialCredits, int TrialDays, CaptchaConfigDto Captcha);

public sealed record SignupRequest(string CompanyName, string FullName, string Email, string Password, bool AcceptTerms, string? CaptchaToken = null, string? Website = null);

public sealed record VerifySignupRequest(string Email, string Token);

public sealed record ContactRequest(string Name, string Email, string? Company, string Message, string? Website = null);

/// <summary>Typed client for the anonymous <c>/api/v1/public</c> endpoints (M11). No credentials are ever sent.</summary>
public interface IPublicApiClient
{
    Task<ApiResult<IReadOnlyList<PublicPlanDto>>> GetPlansAsync(CancellationToken ct = default);

    Task<ApiResult<PublicConfigDto>> GetConfigAsync(CancellationToken ct = default);

    /// <summary>Credit packs with real prices for the pricing page (<c>GET /public/packs</c>). Empty = no pack is on sale.</summary>
    Task<ApiResult<IReadOnlyList<PublicPackDto>>> GetPacksAsync(CancellationToken ct = default);

    /// <summary>202 on success. The answer never says whether the address already exists.</summary>
    Task<ApiResult<bool>> SignupAsync(SignupRequest request, CancellationToken ct = default);

    Task<ApiResult<bool>> VerifySignupAsync(VerifySignupRequest request, CancellationToken ct = default);

    Task<ApiResult<bool>> ContactAsync(ContactRequest request, CancellationToken ct = default);
}

public sealed class PublicApiClient(IApiGateway api) : IPublicApiClient
{
    public Task<ApiResult<IReadOnlyList<PublicPlanDto>>> GetPlansAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<PublicPlanDto>>("public/plans", ct, ApiCallOptions.None);

    public Task<ApiResult<PublicConfigDto>> GetConfigAsync(CancellationToken ct = default) =>
        api.GetAsync<PublicConfigDto>("public/config", ct, ApiCallOptions.None);

    public Task<ApiResult<IReadOnlyList<PublicPackDto>>> GetPacksAsync(CancellationToken ct = default) =>
        api.GetAsync<IReadOnlyList<PublicPackDto>>("public/packs", ct, ApiCallOptions.None);

    public Task<ApiResult<bool>> SignupAsync(SignupRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, "public/signup", request, ct, ApiCallOptions.None);

    public Task<ApiResult<bool>> VerifySignupAsync(VerifySignupRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, "public/signup/verify", request, ct, ApiCallOptions.None);

    public Task<ApiResult<bool>> ContactAsync(ContactRequest request, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, "public/contact", request, ct, ApiCallOptions.None);
}

/// <summary>
/// Plans and settings change rarely and every anonymous visitor asks for them, so successful answers are kept briefly in memory
/// (failures are never cached). Mutations pass straight through.
/// </summary>
public sealed class CachingPublicApiClient(IPublicApiClient inner, IMemoryCache cache) : IPublicApiClient
{
    public static readonly TimeSpan PlansTtl = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(60);

    public Task<ApiResult<IReadOnlyList<PublicPlanDto>>> GetPlansAsync(CancellationToken ct = default) =>
        CachedAsync("public:plans", PlansTtl, () => inner.GetPlansAsync(ct));

    public Task<ApiResult<PublicConfigDto>> GetConfigAsync(CancellationToken ct = default) =>
        CachedAsync("public:config", ConfigTtl, () => inner.GetConfigAsync(ct));

    public Task<ApiResult<IReadOnlyList<PublicPackDto>>> GetPacksAsync(CancellationToken ct = default) =>
        CachedAsync("public:packs", PlansTtl, () => inner.GetPacksAsync(ct));

    public Task<ApiResult<bool>> SignupAsync(SignupRequest request, CancellationToken ct = default) => inner.SignupAsync(request, ct);

    public Task<ApiResult<bool>> VerifySignupAsync(VerifySignupRequest request, CancellationToken ct = default) => inner.VerifySignupAsync(request, ct);

    public Task<ApiResult<bool>> ContactAsync(ContactRequest request, CancellationToken ct = default) => inner.ContactAsync(request, ct);

    private async Task<ApiResult<T>> CachedAsync<T>(string key, TimeSpan ttl, Func<Task<ApiResult<T>>> load)
    {
        if (cache.TryGetValue(key, out ApiResult<T>? hit) && hit is not null)
        {
            return hit;
        }

        var result = await load();
        if (result.IsSuccess)
        {
            cache.Set(key, result, ttl);
        }

        return result;
    }
}
