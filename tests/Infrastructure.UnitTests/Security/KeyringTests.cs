using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Infrastructure.Security;

namespace NexaVerify.Infrastructure.UnitTests.Security;

public class KeyringTests
{
    private static byte[] Key(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static string B64(byte fill) => Convert.ToBase64String(Key(fill));

    private static EncryptionOptions Ring(string? current, params (string Id, byte Fill)[] keys) => new()
    {
        Keys = keys.Select(k => new EncryptionKeyEntry { Id = k.Id, KeyBase64 = B64(k.Fill) }).ToList(),
        CurrentKeyId = current,
    };

    private static MasterKeyProvider Provider(EncryptionOptions o, ILogger<MasterKeyProvider>? logger = null) =>
        new(Options.Create(o), logger ?? NullLogger<MasterKeyProvider>.Instance);

    private static PlatformCrypto Crypto(EncryptionOptions o) => new(Provider(o));

    // ---- validation ----

    [Fact]
    public void A_two_key_ring_encrypts_with_the_current_key_and_knows_both()
    {
        var provider = Provider(Ring("b", ("a", 1), ("b", 2)));

        provider.CurrentKeyId.ShouldBe("b");
        provider.Keys.Select(k => k.Id).ShouldBe(["a", "b"]);
        provider.Contains("a").ShouldBeTrue();
        provider.Contains("zzz").ShouldBeFalse();
    }

    [Fact]
    public void The_legacy_single_key_setting_is_a_ring_entry_under_its_id()
    {
        var provider = Provider(new EncryptionOptions { MasterKeyBase64 = B64(1), MasterKeyId = "m1" });

        provider.CurrentKeyId.ShouldBe("m1");
        provider.LegacyKeyId.ShouldBe("m1");
        provider.Keys.Single().Id.ShouldBe("m1");
    }

    [Fact]
    public void A_legacy_key_next_to_a_new_key_stays_current_until_CurrentKeyId_says_otherwise()
    {
        var o = Ring(null, ("b", 2));
        o.MasterKeyBase64 = B64(1);
        o.MasterKeyId = "m1";
        Provider(o).CurrentKeyId.ShouldBe("m1");

        o.CurrentKeyId = "b";
        Provider(o).CurrentKeyId.ShouldBe("b");
        Provider(o).Contains("m1").ShouldBeTrue(); // the old key keeps reading old data
    }

    [Fact]
    public void The_same_key_listed_as_legacy_and_in_the_ring_is_accepted_but_different_material_is_not()
    {
        var same = Ring("m1", ("m1", 1));
        same.MasterKeyBase64 = B64(1);
        Provider(same).Keys.Count.ShouldBe(1);

        var different = Ring("m1", ("m1", 9));
        different.MasterKeyBase64 = B64(1);
        Should.Throw<InvalidOperationException>(() => Provider(different)).Message.ShouldContain("different key material");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    public void Unusable_key_material_is_refused(string text)
    {
        var o = new EncryptionOptions { Keys = [new EncryptionKeyEntry { Id = "a", KeyBase64 = text }], CurrentKeyId = "a" };

        Should.Throw<InvalidOperationException>(() => Provider(o));
        new EncryptionOptionsValidator().Validate(null, o).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Keys_must_be_exactly_32_bytes()
    {
        var o = new EncryptionOptions { Keys = [new EncryptionKeyEntry { Id = "a", KeyBase64 = Convert.ToBase64String(new byte[16]) }], CurrentKeyId = "a" };

        Should.Throw<InvalidOperationException>(() => Provider(o)).Message.ShouldContain("32 bytes");
    }

    [Fact]
    public void Duplicate_ids_identical_material_and_bad_ids_are_refused()
    {
        Should.Throw<InvalidOperationException>(() => Provider(Ring("a", ("a", 1), ("a", 2)))).Message.ShouldContain("duplicate key id");
        Should.Throw<InvalidOperationException>(() => Provider(Ring("a", ("a", 1), ("b", 1)))).Message.ShouldContain("identical key material");
        Should.Throw<InvalidOperationException>(() => Provider(Ring("a b", ("a b", 1)))).Message.ShouldContain("key id");
        var longId = new string('x', 51);
        Should.Throw<InvalidOperationException>(() => Provider(Ring(longId, (longId, 3)))).Message.ShouldContain("key id");
    }

    [Fact]
    public void The_current_id_must_be_in_the_ring_and_is_required_with_several_keys()
    {
        Should.Throw<InvalidOperationException>(() => Provider(Ring("missing", ("a", 1), ("b", 2)))).Message.ShouldContain("not in the keyring");
        Should.Throw<InvalidOperationException>(() => Provider(Ring(null, ("a", 1), ("b", 2)))).Message.ShouldContain("CurrentKeyId is required");
        Provider(Ring(null, ("only", 1))).CurrentKeyId.ShouldBe("only");
    }

    [Fact]
    public void No_key_at_all_is_an_error_unless_ephemeral_is_allowed()
    {
        Should.Throw<InvalidOperationException>(() => Provider(new EncryptionOptions()));
        var ephemeral = Provider(new EncryptionOptions { AllowEphemeralKey = true });
        ephemeral.IsEphemeral.ShouldBeTrue();
        ephemeral.CurrentKeyId.ShouldBe("m1");
    }

    [Fact]
    public void Logs_and_errors_carry_a_fingerprint_never_key_material()
    {
        var logger = new CapturingLogger<MasterKeyProvider>();
        var secret = Key(7);
        var provider = Provider(Ring("a", ("a", 7)), logger);

        var fingerprint = Convert.ToHexString(SHA256.HashData(secret))[..8].ToLowerInvariant();
        provider.Keys.Single().Fingerprint.ShouldBe(fingerprint);
        logger.Text.ShouldContain(fingerprint);
        logger.Text.ShouldNotContain(B64(7));
        logger.Text.ShouldNotContain(Convert.ToHexString(secret), Case.Insensitive);

        var bad = Should.Throw<InvalidOperationException>(() => Provider(new EncryptionOptions { Keys = [new EncryptionKeyEntry { Id = "a", KeyBase64 = B64(7) + "AA" }], CurrentKeyId = "a" }));
        bad.Message.ShouldNotContain(B64(7));
    }

    // ---- envelope header ----

    [Fact]
    public void The_envelope_header_round_trips_and_rejects_malformed_input()
    {
        var buffer = new byte[KeyEnvelope.HeaderLength("2026-q1") + 5];
        var written = KeyEnvelope.WriteHeader("2026-q1", buffer);

        KeyEnvelope.TryReadHeader(buffer, out var id, out var offset).ShouldBeTrue();
        id.ShouldBe("2026-q1");
        offset.ShouldBe(written);

        KeyEnvelope.TryReadHeader(new byte[40], out _, out _).ShouldBeFalse(); // no magic
        KeyEnvelope.TryReadHeader(buffer.AsSpan(0, 5), out _, out _).ShouldBeFalse(); // truncated
        var badLength = (byte[])buffer.Clone();
        badLength[4] = 200;
        KeyEnvelope.TryReadHeader(badLength, out _, out _).ShouldBeFalse();
        var badChar = (byte[])buffer.Clone();
        badChar[5] = (byte)'/';
        KeyEnvelope.TryReadHeader(badChar, out _, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => KeyEnvelope.WriteHeader("bad id", new byte[64]));
        Should.Throw<ArgumentException>(() => KeyEnvelope.WriteHeader(string.Empty, new byte[64]));
    }

    // ---- platform crypto across a rotation ----

    [Fact]
    public void Data_sealed_under_A_stays_readable_after_the_current_key_becomes_B_and_is_re_keyed()
    {
        var onlyA = Crypto(Ring("a", ("a", 1)));
        var sealedA = onlyA.Protect("totp-secret"u8, CryptoPurposes.MfaSecret, "user-1");

        var ring = Crypto(Ring("b", ("a", 1), ("b", 2)));
        ring.KeyIdOf(sealedA).ShouldBe("a");
        ring.Unprotect(sealedA, CryptoPurposes.MfaSecret, "user-1").ShouldBe("totp-secret"u8.ToArray());

        var sealedB = ring.Reprotect(sealedA, CryptoPurposes.MfaSecret, "user-1");
        ring.KeyIdOf(sealedB).ShouldBe("b");
        ring.Protect("x"u8, CryptoPurposes.MfaSecret, "u").ShouldNotBe(sealedB);

        // with only B in the ring (A retired) the re-keyed payload still opens; the old one fails loudly
        var onlyB = Crypto(Ring("b", ("b", 2)));
        onlyB.Unprotect(sealedB, CryptoPurposes.MfaSecret, "user-1").ShouldBe("totp-secret"u8.ToArray());
        var unknown = Should.Throw<UnknownMasterKeyException>(() => onlyB.Unprotect(sealedA, CryptoPurposes.MfaSecret, "user-1"));
        unknown.KeyId.ShouldBe("a");
        unknown.Message.ShouldContain("'a'");
    }

    [Fact]
    public void An_unknown_key_id_never_falls_back_to_another_key()
    {
        var sealedA = Crypto(Ring("a", ("a", 1))).Protect("s"u8, CryptoPurposes.MfaSecret, "c");
        var other = Crypto(Ring("b", ("b", 2), ("c", 3)));

        Should.Throw<UnknownMasterKeyException>(() => other.Unprotect(sealedA, CryptoPurposes.MfaSecret, "c"));
        Should.Throw<UnknownMasterKeyException>(() => other.ComputeMac(CryptoPurposes.LedgerAnchor, "x"u8, "a"));
        other.HasKey("a").ShouldBeFalse();
    }

    [Fact]
    public void Tampering_wrong_key_wrong_context_and_a_relabelled_header_fail()
    {
        var crypto = Crypto(Ring("b", ("a", 1), ("b", 2)));
        var sealedB = crypto.Protect("secret"u8, CryptoPurposes.MfaSecret, "ctx");

        foreach (var index in new[] { sealedB.Length - 1, sealedB.Length - 20, KeyEnvelope.HeaderLength("b") + 2 })
        {
            var tampered = (byte[])sealedB.Clone();
            tampered[index] ^= 1;
            Should.Throw<CryptographicException>(() => crypto.Unprotect(tampered, CryptoPurposes.MfaSecret, "ctx"));
        }

        Should.Throw<CryptographicException>(() => crypto.Unprotect(sealedB, CryptoPurposes.MfaSecret, "other-ctx"));
        Should.Throw<CryptographicException>(() => crypto.Unprotect(sealedB, CryptoPurposes.LedgerAnchor, "ctx"));

        // pointing the header at the other (known) key must not make it decrypt
        var relabelled = (byte[])sealedB.Clone();
        relabelled[5] = (byte)'a';
        Should.Throw<CryptographicException>(() => crypto.Unprotect(relabelled, CryptoPurposes.MfaSecret, "ctx"));
        Should.Throw<CryptographicException>(() => crypto.Unprotect(new byte[8], CryptoPurposes.MfaSecret, "ctx"));
    }

    [Fact]
    public void Payloads_written_before_the_keyring_existed_are_read_with_the_legacy_key()
    {
        // The exact pre-keyring format: nonce(12) | tag(16) | ciphertext, key = HKDF(master, "nexaverify/platform/<purpose>"), AAD "plat|purpose|context".
        var master = Key(5);
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: Encoding.UTF8.GetBytes("nexaverify/platform/" + CryptoPurposes.MfaSecret));
        var legacy = new byte[12 + 16 + 6];
        RandomNumberGenerator.Fill(legacy.AsSpan(0, 12));
        using (var aes = new AesGcm(derived, 16))
        {
            aes.Encrypt(legacy.AsSpan(0, 12), "legacy"u8, legacy.AsSpan(28), legacy.AsSpan(12, 16), Encoding.UTF8.GetBytes($"plat|{CryptoPurposes.MfaSecret}|user-9"));
        }

        var o = Ring("b", ("b", 6));
        o.MasterKeyBase64 = Convert.ToBase64String(master);
        o.MasterKeyId = "m1";
        var crypto = Crypto(o); // legacy key m1 + new key b (current)

        crypto.KeyIdOf(legacy).ShouldBe("m1");
        Encoding.UTF8.GetString(crypto.Unprotect(legacy, CryptoPurposes.MfaSecret, "user-9")).ShouldBe("legacy");
        crypto.KeyIdOf(crypto.Reprotect(legacy, CryptoPurposes.MfaSecret, "user-9")).ShouldBe("b");

        // legacy payloads need the legacy key: if it is gone the error names it
        Should.Throw<UnknownMasterKeyException>(() => Crypto(Ring("b", ("b", 6))).Unprotect(legacy, CryptoPurposes.MfaSecret, "user-9")).KeyId.ShouldBe("m1");
    }

    [Fact]
    public void Macs_are_per_key_and_per_purpose()
    {
        var crypto = Crypto(Ring("b", ("a", 1), ("b", 2)));
        var data = "checkpoint"u8.ToArray();

        crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data).ShouldBe(crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data, "b"));
        crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data, "a").ShouldNotBe(crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data, "b"));
        crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data, "a").ShouldBe(Crypto(Ring("a", ("a", 1))).ComputeMac(CryptoPurposes.LedgerAnchor, data));
        crypto.ComputeMac(CryptoPurposes.MfaSecret, data, "a").ShouldNotBe(crypto.ComputeMac(CryptoPurposes.LedgerAnchor, data, "a"));
    }

    [Fact]
    public void Wrapped_data_keys_bind_client_version_and_key_id()
    {
        var provider = Provider(Ring("b", ("a", 1), ("b", 2)));
        var client = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = DataKeyWrapper.Wrap(provider, "a", client, 1, dataKey);

        DataKeyWrapper.Unwrap(provider, "a", client, 1, wrapped).ShouldBe(dataKey);
        Should.Throw<CryptographicException>(() => DataKeyWrapper.Unwrap(provider, "b", client, 1, wrapped)); // the id is in the authenticated data
        Should.Throw<CryptographicException>(() => DataKeyWrapper.Unwrap(provider, "a", Guid.NewGuid(), 1, wrapped));
        Should.Throw<CryptographicException>(() => DataKeyWrapper.Unwrap(provider, "a", client, 2, wrapped));
        Should.Throw<UnknownMasterKeyException>(() => DataKeyWrapper.Unwrap(provider, "zz", client, 1, wrapped));
        Should.Throw<CryptographicException>(() => DataKeyWrapper.Unwrap(provider, "a", client, 1, new byte[5]));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public string Text => string.Join('\n', _lines);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines.Add(formatter(state, exception));
    }
}
