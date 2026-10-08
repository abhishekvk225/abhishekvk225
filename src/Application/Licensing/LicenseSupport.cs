using System.Security.Cryptography;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Persistence;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Application.Licensing;

/// <summary>Human-friendly, unambiguous license keys: <c>NXV-XXXXX-XXXXX-XXXXX-XXXXX</c> from a CSPRNG (an identifier, not a secret).</summary>
public static class LicenseKeyGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I, O, 0, 1

    public static string Generate()
    {
        var groups = Enumerable.Range(0, 4)
            .Select(_ => new string(Enumerable.Range(0, 5).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray()));
        return "NXV-" + string.Join('-', groups);
    }
}

/// <summary>
/// The single place that appends to the ledger. It must be called inside the transaction that already changed (and so locked)
/// the license row: that lock serialises writers per license, which is what makes reading the chain tail safe.
/// </summary>
public sealed class LedgerWriter
{
    private readonly ILedgerRepository _ledger;
    private readonly ICurrentUser _currentUser;
    private readonly IRequestInfo _request;
    private readonly TimeProvider _time;

    public LedgerWriter(ILedgerRepository ledger, ICurrentUser currentUser, IRequestInfo request, TimeProvider time)
    {
        _ledger = ledger;
        _currentUser = currentUser;
        _request = request;
        _time = time;
    }

    public async Task<LicenseTransaction> AppendAsync(
        License license,
        LedgerEntryType type,
        int creditsDelta,
        int balanceBefore,
        CancellationToken cancellationToken,
        MeteredOperation? operation = null,
        Guid? recognitionRequestId = null,
        long? referenceTransactionId = null,
        string? idempotencyKey = null,
        string? reason = null)
    {
        var tail = await _ledger.GetTailHashAsync(license.Id, cancellationToken);
        var entry = LicenseTransaction.Create(
            license, type, creditsDelta, balanceBefore, operation, recognitionRequestId, referenceTransactionId, idempotencyKey, reason,
            _currentUser.ActorType switch { ActorType.ApiKey => "ApiKey", ActorType.User => "User", _ => "System" },
            _currentUser.ActorId, _request.CorrelationId, tail, _time.GetUtcNow().UtcDateTime);
        _ledger.Add(entry);
        return entry;
    }
}

internal static class LicenseMapping
{
    public static LicenseDto ToDto(this LicenseRow row, DateTime now)
    {
        var l = row.License;
        return new LicenseDto(
            l.Id, l.ClientId, row.ClientName, l.LicenseKey, l.Name, l.PlanId, row.PlanName, l.Status.ToString(), l.EffectiveStatus(now).ToString(),
            l.TotalCredits, l.ConsumedCredits, l.Remaining, UtilisationPercent(l), l.StartsAt, l.ExpiresAt, DaysRemaining(l, now),
            l.SuspendedAt, l.SuspendedReason, l.Notes, l.CreatedAt, l.UpdatedAt, Convert.ToBase64String(l.RowVersion));
    }

    public static LicenseListItemDto ToListItem(this LicenseRow row, DateTime now)
    {
        var l = row.License;
        return new LicenseListItemDto(
            l.Id, l.ClientId, row.ClientName, l.LicenseKey, l.Name, row.PlanName, l.EffectiveStatus(now).ToString(),
            l.TotalCredits, l.ConsumedCredits, l.Remaining, l.StartsAt, l.ExpiresAt, DaysRemaining(l, now));
    }

    public static LicenseTransactionDto ToDto(this LicenseTransaction t) => new(
        t.Id, t.LicenseId, t.Type.ToString(), t.Credits, t.BalanceBefore, t.BalanceAfter, t.Operation?.ToString(), t.RecognitionRequestId,
        t.ReferenceTransactionId, t.Reason, t.ActorType, t.ActorId, t.CreatedAt);

    public static PlanDto ToDto(this Plan p) => new(
        p.Id, p.Code, p.Name, p.Description, p.DefaultCredits, p.DefaultDurationDays, p.RateLimitPerMinute, p.DailyQuota, p.MaxFaceProfiles,
        p.MaxApiKeys, p.MaxUsers, p.IsActive);

    private static int UtilisationPercent(License l) => l.TotalCredits == 0 ? 0 : (int)Math.Round(100.0 * l.ConsumedCredits / l.TotalCredits);

    private static int DaysRemaining(License l, DateTime now) => Math.Max(0, (int)Math.Ceiling((l.ExpiresAt - now).TotalDays));
}
