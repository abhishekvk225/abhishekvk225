using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Platform;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.UnitTests.Faces;

public class PlatformCryptoAndCacheTests
{
    private static PlatformCrypto Crypto(byte[]? key = null, string id = "m1") =>
        new(new MasterKeyProvider(
            Options.Create(new EncryptionOptions { MasterKeyBase64 = Convert.ToBase64String(key ?? Enumerable.Repeat((byte)7, 32).ToArray()), MasterKeyId = id }),
            NullLogger<MasterKeyProvider>.Instance));

    [Fact]
    public void Protected_data_round_trips_and_is_never_the_plaintext()
    {
        var crypto = Crypto();
        var secret = RandomNumberGenerator.GetBytes(20);

        var a = crypto.Protect(secret, CryptoPurposes.MfaSecret, "user-1");
        var b = crypto.Protect(secret, CryptoPurposes.MfaSecret, "user-1");

        a.Length.ShouldBe(12 + 16 + 20);
        a.ShouldNotBe(b); // a fresh nonce every time
        a.AsSpan().IndexOf(secret.AsSpan()).ShouldBe(-1);
        crypto.Unprotect(a, CryptoPurposes.MfaSecret, "user-1").ShouldBe(secret);
        crypto.Unprotect(b, CryptoPurposes.MfaSecret, "user-1").ShouldBe(secret);
    }

    [Fact]
    public void A_ciphertext_cannot_be_moved_to_another_record_purpose_or_key_and_tampering_is_detected()
    {
        var crypto = Crypto();
        var payload = crypto.Protect(Encoding.UTF8.GetBytes("topsecret"), CryptoPurposes.MfaSecret, "user-1");

        Should.Throw<CryptographicException>(() => crypto.Unprotect(payload, CryptoPurposes.MfaSecret, "user-2"));
        Should.Throw<CryptographicException>(() => crypto.Unprotect(payload, CryptoPurposes.LedgerAnchor, "user-1"));
        Should.Throw<CryptographicException>(() => Crypto(Enumerable.Repeat((byte)8, 32).ToArray()).Unprotect(payload, CryptoPurposes.MfaSecret, "user-1"));
        var flipped = (byte[])payload.Clone();
        flipped[^1] ^= 1;
        Should.Throw<CryptographicException>(() => crypto.Unprotect(flipped, CryptoPurposes.MfaSecret, "user-1"));
        Should.Throw<CryptographicException>(() => crypto.Unprotect(payload.AsSpan(0, 10), CryptoPurposes.MfaSecret, "user-1"));
    }

    [Fact]
    public void The_anchor_mac_is_deterministic_keyed_and_separated_by_purpose()
    {
        var data = Encoding.UTF8.GetBytes("checkpoint");
        var crypto = Crypto();

        var mac = crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data);

        mac.Length.ShouldBe(32);
        crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data).ShouldBe(mac);
        Crypto().ComputeMac(CryptoPurposes.LedgerAnchor, data).ShouldBe(mac); // same master key, same derived key
        crypto.ComputeMac(CryptoPurposes.MfaSecret, data).ShouldNotBe(mac);
        crypto.ComputeMac(CryptoPurposes.LedgerAnchor, Encoding.UTF8.GetBytes("checkpoinT")).ShouldNotBe(mac);
        Crypto(Enumerable.Repeat((byte)9, 32).ToArray()).ComputeMac(CryptoPurposes.LedgerAnchor, data).ShouldNotBe(mac);
        mac.ShouldNotBe(SHA256.HashData(data)); // it is not just a hash anyone can recompute
        Crypto(id: "m2").KeyId.ShouldBe("m2");
    }

    [Fact]
    public void The_cache_of_unknown_key_prefixes_stays_bounded_however_many_are_probed()
    {
        var cache = new ApiKeyNegativeCache(Options.Create(new ApiAuthOptions { NegativeCacheEntries = 100, NegativeCacheSeconds = 60 }));
        var probes = Enumerable.Range(0, 5000).Select(i => "pfx" + i.ToString("D6")).ToList();

        foreach (var probe in probes)
        {
            cache.Add(probe);
        }

        probes.Count(cache.Contains).ShouldBeLessThanOrEqualTo(100);
    }

    [Fact]
    public void A_remembered_unknown_prefix_can_be_forgotten_for_example_after_a_key_is_created()
    {
        var cache = new ApiKeyNegativeCache(Options.Create(new ApiAuthOptions()));

        cache.Add("abc12345");
        cache.Contains("abc12345").ShouldBeTrue();
        cache.Remove("abc12345");

        cache.Contains("abc12345").ShouldBeFalse();
    }
}
