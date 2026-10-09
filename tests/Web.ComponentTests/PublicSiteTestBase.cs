using Microsoft.Extensions.Options;
using NexaVerify.Web.Security;

namespace NexaVerify.Web.ComponentTests;

/// <summary>A scripted <see cref="IPublicApiClient"/> that records what the pages sent.</summary>
public sealed class FakePublicApi : IPublicApiClient
{
    public static readonly PublicPlanDto Trial = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Free trial", "Try it.", 200, 14, ["200 credits", "No card"], true, "Free");

    public static readonly PublicPlanDto Paid = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Business", "Busy teams.", 50000, 365, ["Priority support"], false, null);

    public Func<ApiResult<IReadOnlyList<PublicPlanDto>>> Plans { get; set; } = () => ApiResult<IReadOnlyList<PublicPlanDto>>.Ok([Trial, Paid]);

    public Func<ApiResult<PublicConfigDto>> Config { get; set; } = () => ApiResult<PublicConfigDto>.Ok(new PublicConfigDto(true, 200, 14, new CaptchaConfigDto("none", null)));

    public Func<SignupRequest, ApiResult<bool>> Signup { get; set; } = _ => ApiResult<bool>.Ok(true);

    public Func<VerifySignupRequest, ApiResult<bool>> Verify { get; set; } = _ => ApiResult<bool>.Ok(true);

    public Func<ContactRequest, ApiResult<bool>> Contact { get; set; } = _ => ApiResult<bool>.Ok(true);

    public List<SignupRequest> Signups { get; } = [];

    public List<VerifySignupRequest> Verifications { get; } = [];

    public List<ContactRequest> Contacts { get; } = [];

    public int PlanCalls { get; private set; }

    public Task<ApiResult<IReadOnlyList<PublicPlanDto>>> GetPlansAsync(CancellationToken ct = default)
    {
        PlanCalls++;
        return Task.FromResult(Plans());
    }

    public Task<ApiResult<PublicConfigDto>> GetConfigAsync(CancellationToken ct = default) => Task.FromResult(Config());

    public Task<ApiResult<bool>> SignupAsync(SignupRequest request, CancellationToken ct = default)
    {
        Signups.Add(request);
        return Task.FromResult(Signup(request));
    }

    public Task<ApiResult<bool>> VerifySignupAsync(VerifySignupRequest request, CancellationToken ct = default)
    {
        Verifications.Add(request);
        return Task.FromResult(Verify(request));
    }

    public Task<ApiResult<bool>> ContactAsync(ContactRequest request, CancellationToken ct = default)
    {
        Contacts.Add(request);
        return Task.FromResult(Contact(request));
    }
}

/// <summary>bUnit setup for the public website pages: a fake public API and the site services they inject.</summary>
public abstract class PublicSiteTestBase : UiTestBase
{
    protected PublicSiteTestBase()
    {
        Services.AddSingleton(Api);
        Services.AddSingleton<IPublicApiClient>(Api);
        Services.AddSingleton(Options.Create(new SiteOptions { PublicBaseUrl = "https://www.example.test" }));
        Services.AddSingleton<SiteUrls>();
    }

    protected FakePublicApi Api { get; } = new();

    /// <summary>Fills a text input the way a browser change event would.</summary>
    protected static void Fill(IRenderedComponent<IComponent> cut, string selector, string value) => cut.Find(selector).Change(value);

    public static ApiError Problem(int status, string message = "Something went wrong on our side. Please try again.", string? correlation = "corr-pub", IReadOnlyDictionary<string, string[]>? fields = null) =>
        new(status == 429 ? "RATE_LIMITED" : "INTERNAL_ERROR", message, correlation, status, fields);
}
