using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Infrastructure.Security;
using NexaVerify.TestSupport;

namespace NexaVerify.Infrastructure.IntegrationTests.Security;

[Collection(SqlServerCollection.Name)]
public class ClientKeyServiceTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private TestDb _db = null!;
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private readonly byte[] _masterKey = RandomNumberGenerator.GetBytes(32);

    public ClientKeyServiceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync(_fixture);
        foreach (var id in new[] { _a, _b })
        {
            await _db.EnsureClientAsync(id);
            await ProvisionAsync(id);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ClientKeyService Create(TestDbContext context, byte[]? master = null) => new(
        context,
        new MasterKeyProvider(Options.Create(new EncryptionOptions { MasterKeyBase64 = Convert.ToBase64String(master ?? _masterKey) }), NullLogger<MasterKeyProvider>.Instance),
        new MemoryCache(new MemoryCacheOptions()),
        _db.Time);

    private async Task ProvisionAsync(Guid clientId)
    {
        using var scope = _db.Tenant.BeginPlatform("test: provision");
        await using var context = _db.NewContext();
        await Create(context).ProvisionAsync(clientId, default);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Round_trips_and_never_stores_plaintext_keys()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        var service = Create(context);
        var secret = "embedding-bytes-0123456789"u8.ToArray();

        var cipher = await service.EncryptAsync(_a, secret, "face-template", default);
        var plain = await service.DecryptAsync(_a, cipher, "face-template", default);

        plain.ShouldBe(secret);
        cipher.ShouldNotContain(secret[0]); // sanity: not a trivial copy
        cipher.AsSpan().IndexOf(secret).ShouldBe(-1);
        var stored = await context.ClientKeys.AsNoTracking().SingleAsync();
        stored.WrappedDataKey.Length.ShouldBeGreaterThan(32); // wrapped (nonce+tag+key), not a bare key
        stored.Status.ShouldBe(Domain.Tenancy.ClientKeyStatus.Active);
    }

    [Fact]
    public async Task The_same_plaintext_encrypts_differently_each_time()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        var service = Create(context);

        var one = await service.EncryptAsync(_a, "same"u8.ToArray(), "p", default);
        var two = await service.EncryptAsync(_a, "same"u8.ToArray(), "p", default);

        one.ShouldNotBe(two);
    }

    [Fact]
    public async Task Ciphertext_cannot_be_moved_to_another_client_or_purpose()
    {
        byte[] cipher;
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            cipher = await Create(context).EncryptAsync(_a, "secret"u8.ToArray(), "face-template", default);
        }

        using (_db.Tenant.BeginTenant(_b))
        {
            await using var context = _db.NewContext();
            // B has its own key: the payload was sealed under A's key and A's id
            await Should.ThrowAsync<CryptographicException>(() => Create(context).DecryptAsync(_b, cipher, "face-template", default));
        }

        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            await Should.ThrowAsync<CryptographicException>(() => Create(context).DecryptAsync(_a, cipher, "api-key", default)); // wrong purpose
        }
    }

    [Fact]
    public async Task Tampering_with_the_ciphertext_is_detected()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();
        var service = Create(context);
        var cipher = await service.EncryptAsync(_a, "secret"u8.ToArray(), "p", default);

        foreach (var index in new[] { 0, 5, 20, cipher.Length - 1 })
        {
            var tampered = (byte[])cipher.Clone();
            tampered[index] ^= 0x01;
            await Should.ThrowAsync<CryptographicException>(() => service.DecryptAsync(_a, tampered, "p", default));
        }

        await Should.ThrowAsync<CryptographicException>(() => service.DecryptAsync(_a, new byte[10], "p", default));
    }

    [Fact]
    public async Task A_wrong_master_key_cannot_unwrap_client_keys()
    {
        using var scope = _db.Tenant.BeginTenant(_a);
        await using var context = _db.NewContext();

        await Should.ThrowAsync<CryptographicException>(() =>
            Create(context, RandomNumberGenerator.GetBytes(32)).EncryptAsync(_a, "x"u8.ToArray(), "p", default));
    }

    [Fact]
    public async Task Destroying_keys_crypto_shreds_the_clients_data()
    {
        byte[] cipher;
        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = _db.NewContext();
            cipher = await Create(context).EncryptAsync(_a, "secret"u8.ToArray(), "p", default);
        }

        using (_db.Tenant.BeginPlatform("offboarding"))
        {
            await using var context = _db.NewContext();
            await Create(context).DestroyKeysAsync(_a, default);
        }

        using (_db.Tenant.BeginTenant(_a))
        {
            await using var context = fresh();
            await Should.ThrowAsync<CryptographicException>(() => Create(context).DecryptAsync(_a, cipher, "p", default));
            await Should.ThrowAsync<CryptographicException>(() => Create(context).EncryptAsync(_a, "new"u8.ToArray(), "p", default));
            (await context.ClientKeys.AsNoTracking().SingleAsync()).WrappedDataKey.ShouldBeEmpty();
        }

        TestDbContext fresh() => _db.NewContext();
    }

    [Fact]
    public void The_master_key_must_be_present_and_32_bytes()
    {
        Should.Throw<InvalidOperationException>(() => new MasterKeyProvider(Options.Create(new EncryptionOptions()), NullLogger<MasterKeyProvider>.Instance));
        Should.Throw<InvalidOperationException>(() => new MasterKeyProvider(
            Options.Create(new EncryptionOptions { MasterKeyBase64 = Convert.ToBase64String(new byte[16]) }), NullLogger<MasterKeyProvider>.Instance));
        new MasterKeyProvider(Options.Create(new EncryptionOptions { AllowEphemeralKey = true }), NullLogger<MasterKeyProvider>.Instance).MasterKeyId.ShouldBe("m1");
    }
}
