using Microsoft.Extensions.Options;

namespace NexaVerify.Application.Identity;

/// <summary>
/// Length-first password policy (NIST 800-63B style): minimum length, a deny-list of very common passwords, and no
/// passwords derived from the user's own email. No arbitrary composition rules.
/// </summary>
public sealed class PasswordPolicy
{
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password12", "password123", "password1234", "passw0rd1234", "123456789012",
        "1234567890123", "qwertyuiop12", "qwerty123456", "letmein12345", "welcome12345", "admin1234567",
        "administrator1", "changeme1234", "iloveyou1234", "monkey123456", "dragon123456", "football12345",
        "baseball12345", "abc123456789", "trustno1trust", "sunshine1234", "princess1234", "000000000000",
        "111111111111", "aaaaaaaaaaaa", "nexaverify123", "nexaverify1234", "p@ssw0rd1234", "p@ssword1234",
    };

    private readonly PasswordPolicyOptions _options;

    public PasswordPolicy(IOptions<PasswordPolicyOptions> options)
    {
        _options = options.Value;
    }

    public int MinLength => _options.MinLength;

    public int MaxLength => _options.MaxLength;

    public IReadOnlyList<string> Validate(string? password, string? email = null)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password))
        {
            problems.Add("Password is required.");
            return problems;
        }

        if (password.Length < _options.MinLength)
        {
            problems.Add($"Password must be at least {_options.MinLength} characters.");
        }

        if (password.Length > _options.MaxLength)
        {
            problems.Add($"Password must be at most {_options.MaxLength} characters.");
        }

        if (Common.Contains(password) || password.Distinct().Count() <= 2)
        {
            problems.Add("Password is too common or too repetitive.");
        }

        if (email is { Length: > 0 })
        {
            var local = email.Split('@')[0];
            if (local.Length >= 4 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("Password must not contain your email name.");
            }
        }

        return problems;
    }
}
