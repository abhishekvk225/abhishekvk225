namespace NexaVerify.Web.Services;

/// <summary>
/// Design-review stub for the public website (<c>Ui:UseStubClients</c>, Development/UiDemo only). Sign-up always "succeeds" unless the
/// email starts with "fail"; a verification token starting with "bad" fails.
/// </summary>
public sealed class StubPublicApiClient : IPublicApiClient
{
    public static IReadOnlyList<PublicPlanDto> SamplePlans { get; } =
    [
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Free trial", "Try NexaVerify with your own team.", 200, 14,
            ["200 credits to start", "All features included", "No card needed"], true, "Free"),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a2"), "Starter", "For one site or a small team.", 5000, 365,
            ["5,000 credits", "Valid for 12 months", "Email support"], false, "Contact us"),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a3"), "Business", "For several sites and busy API traffic.", 50000, 365,
            ["50,000 credits", "Valid for 12 months", "Priority support", "Team roles and MFA"], false, null),
    ];

    public Task<ApiResult<IReadOnlyList<PublicPlanDto>>> GetPlansAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<PublicPlanDto>>.Ok(SamplePlans));

    public static IReadOnlyList<PublicPackDto> SamplePacks { get; } =
    [
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), "Starter pack", "For one site or a small team.", 5000, 365, 4900, "USD", null,
            ["5,000 credits", "Valid for 12 months", "Email support"]),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), "Growth pack", "For busy teams and API traffic.", 25000, 365, 19900, "USD", null,
            ["25,000 credits", "Valid for 12 months", "Priority support"]),
    ];

    public Task<ApiResult<IReadOnlyList<PublicPackDto>>> GetPacksAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<PublicPackDto>>.Ok(SamplePacks));

    public Task<ApiResult<PublicConfigDto>> GetConfigAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PublicConfigDto>.Ok(new PublicConfigDto(true, 200, 14, new CaptchaConfigDto("none", null))));

    public Task<ApiResult<bool>> SignupAsync(SignupRequest request, CancellationToken ct = default) =>
        Task.FromResult(request.Email.StartsWith("fail", StringComparison.OrdinalIgnoreCase)
            ? ApiResult<bool>.Fail("INTERNAL_ERROR", "Something went wrong on our side. Please try again.", "demo-signup", 500)
            : ApiResult<bool>.Ok(true));

    public Task<ApiResult<bool>> VerifySignupAsync(VerifySignupRequest request, CancellationToken ct = default) =>
        Task.FromResult(request.Token.StartsWith("bad", StringComparison.OrdinalIgnoreCase)
            ? ApiResult<bool>.Fail("VALIDATION_FAILED", "That link is not valid.", "demo-verify", 400)
            : ApiResult<bool>.Ok(true));

    public Task<ApiResult<bool>> ContactAsync(ContactRequest request, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<bool>.Ok(true));
}
