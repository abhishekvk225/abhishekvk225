using System.Security.Cryptography;
using System.Text;
using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Licensing;

/// <summary>
/// One immutable ledger row. Rows are only ever inserted (database triggers reject UPDATE/DELETE) and each carries a hash of its
/// own content chained to the previous row of the same license, so any later tampering — including by someone with raw database
/// access — is detectable by recomputing the chain.
/// </summary>
public sealed class LicenseTransaction : ITenantOwned, IAppendOnly
{
    public const int HashSize = 32;

    private LicenseTransaction()
    {
    }

    public long Id { get; private set; }

    public Guid LicenseId { get; private set; }

    public Guid ClientId { get; set; }

    public LedgerEntryType Type { get; private set; }

    /// <summary>Signed change of the license's remaining balance (Consume −n, Grant/Refund/Renewal +n, write-offs −remaining).</summary>
    public int Credits { get; private set; }

    public int BalanceBefore { get; private set; }

    public int BalanceAfter { get; private set; }

    public MeteredOperation? Operation { get; private set; }

    /// <summary>The recognition request this charge belongs to (foreign key arrives with the face module).</summary>
    public Guid? RecognitionRequestId { get; private set; }

    /// <summary>For a refund: the consumption it reverses.</summary>
    public long? ReferenceTransactionId { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public string? Reason { get; private set; }

    public string ActorType { get; private set; } = string.Empty;

    public Guid? ActorId { get; private set; }

    public string? CorrelationId { get; private set; }

    public byte[] PrevHash { get; private set; } = [];

    public byte[] RowHash { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public static byte[] Genesis { get; } = new byte[HashSize];

    public static LicenseTransaction Create(
        License license,
        LedgerEntryType type,
        int credits,
        int balanceBefore,
        MeteredOperation? operation,
        Guid? recognitionRequestId,
        long? referenceTransactionId,
        string? idempotencyKey,
        string? reason,
        string actorType,
        Guid? actorId,
        string? correlationId,
        byte[]? previousHash,
        DateTime now)
    {
        var entry = new LicenseTransaction
        {
            LicenseId = license.Id,
            ClientId = license.ClientId,
            Type = type,
            Credits = credits,
            BalanceBefore = balanceBefore,
            BalanceAfter = balanceBefore + credits,
            Operation = operation,
            RecognitionRequestId = recognitionRequestId,
            ReferenceTransactionId = referenceTransactionId,
            IdempotencyKey = idempotencyKey,
            Reason = reason is { Length: > 500 } ? reason[..500] : reason,
            ActorType = actorType,
            ActorId = actorId,
            CorrelationId = correlationId,
            PrevHash = previousHash ?? Genesis,
            CreatedAt = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc), // the column keeps milliseconds only
        };
        entry.RowHash = entry.ComputeHash();
        return entry;
    }

    /// <summary>Recomputes this row's hash from its content and its predecessor's hash.</summary>
    public byte[] ComputeHash()
    {
        // Canonical, unambiguous serialisation: fixed order, invariant formats, length-prefixed text, explicit nulls.
        var sb = new StringBuilder();
        void Add(string? value) => sb.Append(value is null ? "~" : value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value).Append('|');

        Add(Convert.ToHexString(PrevHash));
        Add(LicenseId.ToString("N"));
        Add(ClientId.ToString("N"));
        Add(Type.ToString());
        Add(Credits.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(BalanceBefore.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(BalanceAfter.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(Operation?.ToString());
        Add(RecognitionRequestId?.ToString("N"));
        Add(ReferenceTransactionId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(IdempotencyKey);
        Add(Reason);
        Add(ActorType);
        Add(ActorId?.ToString("N"));
        Add(CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)); // Kind-independent, matches datetime2(3)
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
