using System.ComponentModel.DataAnnotations;

namespace NexaVerify.Web.Services;

/// <summary>Sign-up form. Mirrors the API rules for instant feedback; the API stays authoritative.</summary>
public sealed class SignupModel
{
    [Required(ErrorMessage = "Enter your company name.")]
    [StringLength(120, ErrorMessage = "Use 120 characters or fewer.")]
    public string CompanyName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your full name.")]
    [StringLength(120, ErrorMessage = "Use 120 characters or fewer.")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your work email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256, ErrorMessage = "Use 256 characters or fewer.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Choose a password.")]
    [MinLength(PasswordRules.MinLength, ErrorMessage = "Use at least 12 characters.")]
    [StringLength(128, ErrorMessage = "Use 128 characters or fewer.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Type the password again.")]
    [Compare(nameof(Password), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Range(typeof(bool), "true", "true", ErrorMessage = "Please accept the terms to continue.")]
    public bool AcceptTerms { get; set; }

    /// <summary>Honeypot. Hidden from people; bots that fill every field reveal themselves. Never validated, never shown.</summary>
    public string? Website { get; set; }
}

public sealed class ContactModel
{
    [Required(ErrorMessage = "Enter your name.")]
    [StringLength(120, ErrorMessage = "Use 120 characters or fewer.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256, ErrorMessage = "Use 256 characters or fewer.")]
    public string Email { get; set; } = string.Empty;

    [StringLength(120, ErrorMessage = "Use 120 characters or fewer.")]
    public string? Company { get; set; }

    [Required(ErrorMessage = "Tell us how we can help.")]
    [StringLength(4000, MinimumLength = 10, ErrorMessage = "Please write between 10 and 4000 characters.")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Honeypot (see <see cref="SignupModel.Website"/>).</summary>
    public string? Website { get; set; }
}

/// <summary>Strength hint next to the password box. Advisory only: the policy is the minimum length and the API decides.</summary>
public static class PasswordStrength
{
    public const int Max = 4;

    /// <summary>0 (empty) to 4. Length matters most; mixing character kinds and avoiding repeats add a little.</summary>
    public static int Score(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return 0;
        }

        var score = 0;
        if (password.Length >= PasswordRules.MinLength)
        {
            score++;
        }

        if (password.Length >= 16)
        {
            score++;
        }

        var kinds = (password.Any(char.IsLower) ? 1 : 0) + (password.Any(char.IsUpper) ? 1 : 0) + (password.Any(char.IsDigit) ? 1 : 0)
                    + (password.Any(c => !char.IsLetterOrDigit(c)) ? 1 : 0);
        if (kinds >= 2 || password.Contains(' '))
        {
            score++;
        }

        if (password.Distinct().Count() >= 8)
        {
            score++;
        }

        return password.Length < PasswordRules.MinLength ? 1 : score;
    }

    public static string Label(int score) => score switch
    {
        0 => "Not started",
        1 => "Too short or too simple",
        2 => "Fair",
        3 => "Good",
        _ => "Strong",
    };
}
