using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Licensing;

/// <summary>A subscription template: defaults for new licenses and the limits that come with it. Global (not tenant-owned).</summary>
public sealed class Plan : AuditableEntity
{
    private Plan()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int DefaultCredits { get; set; }

    public int DefaultDurationDays { get; set; }

    public int RateLimitPerMinute { get; set; }

    public int? DailyQuota { get; set; }

    public int? MaxFaceProfiles { get; set; }

    public int MaxApiKeys { get; set; }

    public int MaxUsers { get; set; }

    /// <summary>Shown on the public website (only active plans are listed).</summary>
    public bool IsPublic { get; set; }

    /// <summary>The plan self-service sign-ups start on; its public credits/period are the configured trial values.</summary>
    public bool IsTrial { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>Optional text shown instead of "Contact us" (no payment is processed).</summary>
    public string? DisplayPrice { get; set; }

    /// <summary>Short marketing bullet points for the public plan card.</summary>
    public List<string> Highlights { get; set; } = [];

    public static Plan Create(string code, string name, int defaultCredits, int defaultDurationDays)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 30 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new DomainException("PLAN_CODE_INVALID", "Plan code must be 1–30 letters, digits or hyphens.");
        }

        if (defaultCredits < 0 || defaultDurationDays <= 0)
        {
            throw new DomainException("PLAN_DEFAULTS_INVALID", "Default credits must be non-negative and the duration positive.");
        }

        return new Plan
        {
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            DefaultCredits = defaultCredits,
            DefaultDurationDays = defaultDurationDays,
            RateLimitPerMinute = 60,
            MaxApiKeys = 5,
            MaxUsers = 10,
        };
    }
}
