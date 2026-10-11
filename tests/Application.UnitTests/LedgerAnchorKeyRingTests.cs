using System.Security.Cryptography;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Licensing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.UnitTests;

/// <summary>Checkpoints are verified with the key they name, so a master-key rotation can re-sign them without losing the evidence.</summary>
public class LedgerAnchorKeyRingTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class RingCrypto : IPlatformCrypto
    {
        private readonly Dictionary<string, byte[]> _keys;

        public RingCrypto(string current, params string[] ids)
        {
            KeyId = current;
            _keys = ids.ToDictionary(id => id, id => System.Text.Encoding.UTF8.GetBytes("key-material-for-" + id + "-0123456789"));
        }

        public string KeyId { get; }

        public bool HasKey(string keyId) => _keys.ContainsKey(keyId);

        public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, string context) => throw new NotSupportedException();

        public byte[] Unprotect(ReadOnlySpan<byte> payload, string purpose, string context) => throw new NotSupportedException();

        public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data) => ComputeMac(purpose, data, KeyId);

        public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data, string keyId) => HMACSHA256.HashData(_keys[keyId], data);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(T0);
    }

    private static LedgerAnchorService Anchors(RingCrypto crypto) => new(crypto, null!, new FixedClock());

    private static LedgerCheckpoint CheckpointUnder(RingCrypto crypto) =>
        LedgerCheckpoint.Create(Guid.NewGuid(), Guid.NewGuid(), 42, 7, RandomNumberGenerator.GetBytes(32), 93, crypto.KeyId, T0, bytes => crypto.ComputeMac(CryptoPurposes.LedgerAnchor, bytes));

    [Fact]
    public void A_checkpoint_is_verified_with_the_key_it_names_not_the_current_one()
    {
        var checkpoint = CheckpointUnder(new RingCrypto("a", "a", "b"));

        Anchors(new RingCrypto("b", "a", "b")).IsAuthentic(checkpoint).ShouldBeTrue(); // current is b, the checkpoint was made with a
    }

    [Fact]
    public void A_checkpoint_naming_a_key_that_is_not_in_the_ring_is_not_authentic_and_says_so()
    {
        var checkpoint = CheckpointUnder(new RingCrypto("a", "a"));

        Anchors(new RingCrypto("b", "b")).IsAuthentic(checkpoint).ShouldBeFalse();
    }

    [Fact]
    public void An_altered_checkpoint_fails_under_its_own_key()
    {
        var crypto = new RingCrypto("a", "a");
        var checkpoint = CheckpointUnder(crypto);
        typeof(LedgerCheckpoint).GetProperty(nameof(LedgerCheckpoint.BalanceAfter))!.SetValue(checkpoint, 1000);

        Anchors(crypto).IsAuthentic(checkpoint).ShouldBeFalse();
    }

    [Fact]
    public void Re_signing_keeps_the_content_and_timestamp_and_changes_only_the_key()
    {
        var old = CheckpointUnder(new RingCrypto("a", "a", "b"));
        var rotated = Anchors(new RingCrypto("b", "a", "b")).Resign(old);

        rotated.KeyId.ShouldBe("b");
        rotated.Mac.ShouldNotBe(old.Mac);
        (rotated.LicenseId, rotated.ClientId, rotated.LastEntryId, rotated.EntryCount, rotated.BalanceAfter, rotated.CreatedAt)
            .ShouldBe((old.LicenseId, old.ClientId, old.LastEntryId, old.EntryCount, old.BalanceAfter, old.CreatedAt));
        rotated.HeadHash.ShouldBe(old.HeadHash);
        Anchors(new RingCrypto("b", "b")).IsAuthentic(rotated).ShouldBeTrue(); // verifies with only the new key: the old one can be retired
        Anchors(new RingCrypto("b", "b")).IsAuthentic(old).ShouldBeFalse();
    }
}
