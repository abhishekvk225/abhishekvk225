using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;

namespace NexaVerify.Infrastructure.Security;

public sealed class PasswordHashingOptions
{
    public const string SectionName = "PasswordHashing";

    /// <summary>PBKDF2-HMAC-SHA512 iterations (OWASP: ≥ 210,000). Raising it makes old hashes upgrade on next login.</summary>
    [Range(10_000, 5_000_000)]
    public int IterationCount { get; set; } = 210_000;
}

/// <summary>Password hashing with ASP.NET Core Identity's PBKDF2 (v3 format: per-hash random salt, versioned, upgradeable).</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private static readonly object User = new();

    private readonly PasswordHasher<object> _hasher;
    private readonly string _dummyHash;

    public Pbkdf2PasswordHasher(IOptions<PasswordHashingOptions> options)
    {
        _hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = options.Value.IterationCount }));
        _dummyHash = _hasher.HashPassword(User, Guid.NewGuid().ToString("N"));
    }

    public string Hash(string password) => _hasher.HashPassword(User, password);

    public PasswordVerification Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(User, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };

    public void BurnTime(string password) => _ = _hasher.VerifyHashedPassword(User, _dummyHash, password);
}
