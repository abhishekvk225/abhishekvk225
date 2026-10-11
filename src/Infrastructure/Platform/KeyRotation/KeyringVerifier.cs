using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;
using NexaVerify.Domain.Tenancy;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.Platform.KeyRotation;

public sealed class KeyringVerification
{
    public required KeyUsageReport Usage { get; init; }

    public int ClientKeysTested { get; set; }

    public int MfaSecretsTested { get; set; }

    public int CheckpointsTested { get; set; }

    /// <summary>Samples that failed to decrypt / verify (counts and ids only).</summary>
    public List<string> Failures { get; } = [];

    public int LicensesChecked { get; set; }

    public long LedgerEntriesChecked { get; set; }

    public int BrokenLicenses { get; set; }

    public string? LedgerError { get; set; }

    /// <summary>0 = healthy, 3 = a ledger or checkpoint is broken, 1 = keys cannot decrypt / the check could not run.</summary>
    public int ExitCode => Failures.Count > 0 || LedgerError is not null || Usage.UnknownKeyIds.Count > 0 ? 1 : BrokenLicenses > 0 ? 3 : 0;
}

/// <summary>Proves the keyring works against the real data: decrypts a sample of everything each key protects, then runs the ledger verification (checkpoints are checked under the key that signed them).</summary>
public sealed class KeyringVerifier
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MasterKeyProvider _master;
    private readonly PlatformCrypto _crypto;
    private readonly KeyUsageSurvey _survey;

    public KeyringVerifier(IServiceScopeFactory scopes, MasterKeyProvider master, PlatformCrypto crypto, KeyUsageSurvey survey)
    {
        _scopes = scopes;
        _master = master;
        _crypto = crypto;
        _survey = survey;
    }

    public async Task<KeyringVerification> VerifyAsync(int samplePerKey, CancellationToken cancellationToken)
    {
        var result = new KeyringVerification { Usage = await _survey.SurveyAsync(cancellationToken) };
        foreach (var unknown in result.Usage.UnknownKeyIds)
        {
            result.Failures.Add($"live data references key id '{unknown}', which is not in the keyring");
        }

        await using (var scope = _scopes.CreateAsyncScope())
        {
            using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("keyring verification: samples");
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var anchors = scope.ServiceProvider.GetRequiredService<LedgerAnchorService>();

            foreach (var keyId in _master.Keys.Select(k => k.Id))
            {
                var keys = await db.ClientKeys.AsNoTracking().Where(k => k.MasterKeyId == keyId && k.Status != ClientKeyStatus.Destroyed)
                    .OrderBy(k => k.Id).Take(samplePerKey).Select(k => new { k.Id, k.ClientId, k.KeyVersion, k.WrappedDataKey }).ToListAsync(cancellationToken);
                foreach (var k in keys)
                {
                    result.ClientKeysTested++;
                    try
                    {
                        CryptographicOperations.ZeroMemory(DataKeyWrapper.Unwrap(_master, keyId, k.ClientId, k.KeyVersion, k.WrappedDataKey));
                    }
                    catch (CryptographicException)
                    {
                        result.Failures.Add($"client data key {k.Id} does not unwrap under key '{keyId}'");
                    }
                }

                var checkpoints = await db.LedgerCheckpoints.AsNoTracking()
                    .Where(c => c.KeyId == keyId && !db.LedgerCheckpointSupersessions.Any(s => s.CheckpointId == c.Id))
                    .OrderBy(c => c.Id).Take(samplePerKey).ToListAsync(cancellationToken);
                foreach (var c in checkpoints)
                {
                    result.CheckpointsTested++;
                    if (!anchors.IsAuthentic(c))
                    {
                        result.Failures.Add($"ledger checkpoint {c.Id} does not verify under key '{keyId}'");
                    }
                }
            }

            // MFA: the key id sits in each payload's header, so take a sample of rows and let the crypto pick the key.
            var mfa = await db.UserMfa.AsNoTracking().OrderBy(m => m.Id).Take(Math.Max(samplePerKey * Math.Max(1, _master.Keys.Count), 1)).Select(m => new { m.Id, m.UserId, m.SecretEnc }).ToListAsync(cancellationToken);
            foreach (var m in mfa)
            {
                result.MfaSecretsTested++;
                try
                {
                    CryptographicOperations.ZeroMemory(_crypto.Unprotect(m.SecretEnc, CryptoPurposes.MfaSecret, CryptoPurposes.MfaSecretContext(m.UserId)));
                }
                catch (CryptographicException)
                {
                    result.Failures.Add($"MFA secret {m.Id} does not decrypt (key '{_crypto.KeyIdOf(m.SecretEnc)}')");
                }
            }
        }

        // The full ledger check, which also validates every checkpoint's MAC under its own key and re-anchors clean heads under the current key.
        await using var ledgerScope = _scopes.CreateAsyncScope();
        using var ledgerPlatform = ledgerScope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("keyring verification: ledger");
        var verified = await ledgerScope.ServiceProvider.GetRequiredService<ILedgerVerificationService>().RunAsync("keyring-verify", null, cancellationToken);
        if (verified.IsFailure)
        {
            result.LedgerError = verified.Error!.Message;
        }
        else
        {
            result.LicensesChecked = verified.Value.LicensesChecked;
            result.LedgerEntriesChecked = verified.Value.EntriesChecked;
            result.BrokenLicenses = verified.Value.BrokenLicenses;
        }

        return result;
    }
}
