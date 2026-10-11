using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexaVerify.Application;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Persistence;
using NexaVerify.Domain.Identity;
using NexaVerify.Domain.Licensing;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.Infrastructure.Platform.KeyRotation;
using NexaVerify.Infrastructure.Security;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Security;

/// <summary>
/// A "deployment": the real application and infrastructure services wired against a test database with a given master keyring. Several
/// harnesses over the same database stand for the same data seen by different configurations (before / during / after a rotation).
/// </summary>
internal sealed class KeyRotationHarness : IAsyncDisposable
{
    private readonly TestDb _db;

    private KeyRotationHarness(TestDb db, ServiceProvider services)
    {
        _db = db;
        Services = services;
    }

    public ServiceProvider Services { get; }

    public MasterKeyProvider Master => Services.GetRequiredService<MasterKeyProvider>();

    public PlatformCrypto Crypto => Services.GetRequiredService<PlatformCrypto>();

    public static byte[] KeyBytes(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    /// <summary>A ring of (id, fill) keys with the given current id.</summary>
    public static Dictionary<string, string?> Ring(string current, params (string Id, byte Fill)[] keys)
    {
        var settings = new Dictionary<string, string?> { ["Encryption:CurrentKeyId"] = current };
        for (var i = 0; i < keys.Length; i++)
        {
            settings[$"Encryption:Keys:{i}:Id"] = keys[i].Id;
            settings[$"Encryption:Keys:{i}:KeyBase64"] = Convert.ToBase64String(KeyBytes(keys[i].Fill));
        }

        return settings;
    }

    /// <summary>The pre-keyring configuration: Encryption:MasterKeyBase64 (+ MasterKeyId).</summary>
    public static Dictionary<string, string?> Legacy(byte fill, string id = "m1") => new()
    {
        ["Encryption:MasterKeyBase64"] = Convert.ToBase64String(KeyBytes(fill)),
        ["Encryption:MasterKeyId"] = id,
    };

    public static KeyRotationHarness Create(TestDb db, Dictionary<string, string?> encryption)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = db.ConnectionString,
            ["Jwt:AllowEphemeralKey"] = "true",
        };
        foreach (var pair in encryption)
        {
            settings[pair.Key] = pair.Value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddApplication();
        services.AddInfrastructure(configuration);
        return new KeyRotationHarness(db, services.BuildServiceProvider());
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    // ------------------------------------------------------------------ scopes

    public async Task WithPlatformAsync(Func<IServiceProvider, AppDbContext, Task> work)
    {
        await using var scope = Services.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("test: key rotation");
        await work(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> WithPlatformAsync<T>(Func<IServiceProvider, AppDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        using var platform = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginPlatform("test: key rotation");
        return await work(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ------------------------------------------------------------------ seeding (written under THIS harness's current key)

    public async Task AddClientAsync(Guid clientId)
    {
        await _db.EnsureClientAsync(clientId);
        await WithPlatformAsync(async (sp, db) =>
        {
            await sp.GetRequiredService<IClientKeyProvisioner>().ProvisionAsync(clientId, default);
            await db.SaveChangesAsync();
        });
    }

    public async Task<byte[]> EncryptForClientAsync(Guid clientId, byte[] plaintext)
    {
        await using var scope = Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
        return await scope.ServiceProvider.GetRequiredService<IClientEncryption>().EncryptAsync(clientId, plaintext, "face-template", default);
    }

    /// <summary>Decrypts like a request would, but with a cold key cache so the wrapped key is read and unwrapped from the database.</summary>
    public async Task<byte[]> DecryptForClientColdAsync(Guid clientId, byte[] payload)
    {
        await using var scope = Services.CreateAsyncScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScope>().BeginTenant(clientId);
        var service = new ClientKeyService(scope.ServiceProvider.GetRequiredService<AppDbContext>(), Master, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), TimeProvider.System);
        return await service.DecryptAsync(clientId, payload, "face-template", default);
    }

    public static string MfaPlaintext(Guid userId) => "totp-" + userId.ToString("N");

    public async Task<Guid> AddMfaUserAsync(Guid clientId, string email)
    {
        var userId = Guid.Empty;
        await WithPlatformAsync(async (sp, db) =>
        {
            var user = User.Create(email, "Test User", "hash", clientId, false, false);
            db.Users.Add(user);
            var secret = Encoding.UTF8.GetBytes(MfaPlaintext(user.Id));
            db.UserMfa.Add(UserMfa.StartEnrolment(user, sp.GetRequiredService<IPlatformCrypto>().Protect(secret, CryptoPurposes.MfaSecret, CryptoPurposes.MfaSecretContext(user.Id)), DateTime.UtcNow));
            await db.SaveChangesAsync();
            userId = user.Id;
        });
        return userId;
    }

    /// <summary>Inserts an MFA row in the exact pre-keyring payload format (no key-id header), as old deployments hold them.</summary>
    public async Task<Guid> AddLegacyFormatMfaUserAsync(Guid clientId, string email, byte masterFill)
    {
        var userId = Guid.Empty;
        await WithPlatformAsync(async (sp, db) =>
        {
            var user = User.Create(email, "Legacy User", "hash", clientId, false, false);
            db.Users.Add(user);
            var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, KeyBytes(masterFill), 32, salt: null, info: Encoding.UTF8.GetBytes("nexaverify/platform/" + CryptoPurposes.MfaSecret));
            var plain = Encoding.UTF8.GetBytes(MfaPlaintext(user.Id));
            var payload = new byte[12 + 16 + plain.Length];
            RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
            using (var aes = new AesGcm(derived, 16))
            {
                aes.Encrypt(payload.AsSpan(0, 12), plain, payload.AsSpan(28), payload.AsSpan(12, 16), Encoding.UTF8.GetBytes($"plat|{CryptoPurposes.MfaSecret}|{CryptoPurposes.MfaSecretContext(user.Id)}"));
            }

            db.UserMfa.Add(UserMfa.StartEnrolment(user, payload, DateTime.UtcNow));
            await db.SaveChangesAsync();
            userId = user.Id;
        });
        return userId;
    }

    public async Task<string> ReadMfaAsync(Guid userId) =>
        await WithPlatformAsync(async (sp, db) =>
        {
            var row = await db.UserMfa.AsNoTracking().SingleAsync(m => m.UserId == userId);
            return Encoding.UTF8.GetString(sp.GetRequiredService<IPlatformCrypto>().Unprotect(row.SecretEnc, CryptoPurposes.MfaSecret, CryptoPurposes.MfaSecretContext(userId)));
        });

    /// <summary>A license with a real hash-chained ledger of <paramref name="entries"/> rows, anchored (twice, so it has two checkpoints) under this harness's current key.</summary>
    public async Task<Guid> AddAnchoredLicenseAsync(Guid clientId, int entries = 6)
    {
        var licenseId = Guid.Empty;
        await WithPlatformAsync(async (sp, db) =>
        {
            var license = License.Create(clientId, "Rotation test", null, "NXV-" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant(), 1000, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(60));
            db.Licenses.Add(license);
            var grant = LicenseTransaction.Create(license, LedgerEntryType.Grant, 1000, 0, null, null, null, null, "seed", "System", null, null, null, DateTime.UtcNow);
            db.LicenseTransactions.Add(grant);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        });
        await AppendAsync(licenseId, entries / 2);
        await VerifyAndAnchorAsync();
        await AppendAsync(licenseId, entries - entries / 2);
        await VerifyAndAnchorAsync();
        return licenseId;
    }

    public async Task AppendAsync(Guid licenseId, int count)
    {
        await WithPlatformAsync(async (sp, db) =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == licenseId);
            var last = await db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == licenseId).OrderByDescending(t => t.Id).FirstAsync();
            var previous = last.RowHash;
            for (var i = 0; i < count; i++)
            {
                var entry = LicenseTransaction.Create(license, LedgerEntryType.Consume, -1, license.Remaining, MeteredOperation.Verify, null, null, null, "seed", "System", null, null, previous, DateTime.UtcNow);
                typeof(License).GetProperty(nameof(License.ConsumedCredits))!.SetValue(license, license.ConsumedCredits + 1);
                db.LicenseTransactions.Add(entry);
                previous = entry.RowHash;
            }

            await db.SaveChangesAsync();
        });
    }

    public async Task<LedgerRunSummary> VerifyAndAnchorAsync()
    {
        return await WithPlatformAsync(async (sp, _) =>
        {
            var result = await sp.GetRequiredService<ILedgerVerificationService>().RunAsync("test", null, default);
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            return new LedgerRunSummary(result.Value.LicensesChecked, result.Value.BrokenLicenses);
        });
    }

    public async Task TamperWithLedgerAsync(Guid licenseId)
    {
        await WithPlatformAsync(async (sp, db) =>
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions DISABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            try
            {
                var entryId = await db.LicenseTransactions.AsNoTracking().Where(t => t.LicenseId == licenseId && t.Type == LedgerEntryType.Consume).OrderBy(t => t.Id).Select(t => t.Id).FirstAsync();
                await db.Database.ExecuteSqlRawAsync("UPDATE licensing.LicenseTransactions SET Credits = Credits - 7 WHERE Id = {0}", entryId);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE licensing.LicenseTransactions ENABLE TRIGGER trg_LicenseTransactions_AppendOnly");
            }
        });
    }

    public Task<KeyUsageReport> UsageAsync() => Services.GetRequiredService<KeyUsageSurvey>().SurveyAsync(default);

    public Task<RotationReport> RotateAsync(int batchSize = 200, int? maxBatches = null, bool dryRun = false, CancellationToken ct = default) =>
        Services.GetRequiredService<MasterKeyRotationService>().RunAsync(new RotationRequest(dryRun, batchSize, maxBatches), ct);

    public async Task<long> CountAsync<T>(Func<AppDbContext, IQueryable<T>> query) =>
        await WithPlatformAsync(async (_, db) => (long)await query(db).CountAsync());
}

internal sealed record LedgerRunSummary(int Licenses, int Broken);
