using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Application.Tenancy;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Identity;

namespace NexaVerify.Application.Identity;

public sealed class MfaOptions
{
    public const string SectionName = "Mfa";

    /// <summary>Master switch for the whole MFA feature (enrolment, challenge, enforcement).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Name shown in the authenticator app next to the account.</summary>
    [Required]
    [StringLength(40, MinimumLength = 1)]
    public string Issuer { get; set; } = "NexaVerify";

    /// <summary>Lifetime of the ticket between "password accepted" and "second factor proven".</summary>
    [Range(1, 30)]
    public int ChallengeMinutes { get; set; } = 5;

    /// <summary>Codes allowed per challenge (right or wrong); the next one finds the challenge dead.</summary>
    [Range(1, 10)]
    public int MaxChallengeAttempts { get; set; } = 5;

    /// <summary>Accepted clock drift in 30-second steps either side of "now".</summary>
    [Range(0, 2)]
    public int Window { get; set; } = 1;

    [Range(4, 20)]
    public int RecoveryCodeCount { get; set; } = 10;

    /// <summary>
    /// Platform default of <c>security.requireMfa</c> for staff: accounts holding one of these roles must enrol before they can do
    /// anything else, as a comma-separated list. Empty = never required. Client accounts are governed by each client's own
    /// <c>security.requireMfa</c> setting.
    /// </summary>
    public string RequiredPlatformRoles { get; set; } = SystemRoles.SuperAdmin;

    public IReadOnlySet<string> RequiredRoleSet() =>
        RequiredPlatformRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether client users may enrol at all (and so be required to by their client's setting).</summary>
    public bool AllowClientUsers { get; set; } = true;
}

/// <summary>Decides whether MFA applies to an account and whether it still has to be set up.</summary>
public interface IMfaPolicy
{
    /// <summary>MFA can be used by this account at all (feature on, and client users allowed).</summary>
    bool IsAvailable(User user);

    /// <summary>The account must enrol: required by policy and not yet enrolled.</summary>
    Task<bool> EnrolmentRequiredAsync(User user, IReadOnlyCollection<string> roles, CancellationToken cancellationToken);
}

public sealed class MfaPolicy : IMfaPolicy
{
    private readonly MfaOptions _options;
    private readonly IClientSettingsService _settings;

    public MfaPolicy(IOptions<MfaOptions> options, IClientSettingsService settings)
    {
        _options = options.Value;
        _settings = settings;
    }

    public bool IsAvailable(User user) => _options.Enabled && (user.IsPlatformUser || _options.AllowClientUsers);

    public async Task<bool> EnrolmentRequiredAsync(User user, IReadOnlyCollection<string> roles, CancellationToken cancellationToken)
    {
        if (!IsAvailable(user) || user.TwoFactorEnabled)
        {
            return false;
        }

        if (user.IsPlatformUser)
        {
            var required = _options.RequiredRoleSet();
            return roles.Any(required.Contains);
        }

        return (await _settings.GetEffectiveAsync(user.ClientId, cancellationToken)).Bool(SettingKeys.Security.RequireMfa);
    }
}
