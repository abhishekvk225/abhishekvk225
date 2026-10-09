using NexaVerify.Contracts.Common;

namespace NexaVerify.Contracts.Public;

/// <summary>A plan shown on the public website. No tenant data; <c>Credits</c>/<c>ValidityDays</c> of the trial plan are the configured trial values.</summary>
public sealed record PublicPlanDto(
    Guid Id, string Name, string? Description, int Credits, int ValidityDays, IReadOnlyList<string> Highlights, bool IsTrial, string? DisplayPrice);

public sealed record CaptchaConfigDto(string Provider, string? SiteKey);

public sealed record PublicConfigDto(bool SignupEnabled, int TrialCredits, int TrialDays, CaptchaConfigDto Captcha);

/// <summary>Self-service sign-up. <c>Website</c> is a honeypot that real visitors never fill in.</summary>
public sealed record SignupRequest(
    string CompanyName, string FullName, string Email, [property: Sensitive] string Password, bool AcceptTerms, [property: Sensitive] string? CaptchaToken = null, string? Website = null);

public sealed record VerifySignupRequest(string Email, [property: Sensitive] string Token);

/// <summary>Asks for the verification email of a pending sign-up again. Always accepted, whatever the address.</summary>
public sealed record ResendSignupRequest(string Email);

/// <summary>A message from the public contact form. <c>Website</c> is a honeypot.</summary>
public sealed record SubmitContactRequest(string Name, string Email, string? Company, string Message, string? Website = null);

public sealed record ContactRequestDto(Guid Id, string Name, string Email, string? Company, string Message, DateTime CreatedAt);
