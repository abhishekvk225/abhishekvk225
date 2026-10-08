using System.Globalization;
using System.Text;
using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Licensing;

/// <summary>
/// A signed snapshot of one license's ledger head, written only after the whole chain verified clean. The MAC is HMAC-SHA256 under a
/// key that lives outside the database (the master key provider, purpose "ledger-anchor"), so someone who can rewrite ledger rows
/// AND recompute the plain SHA-256 chain still cannot produce a matching checkpoint. Rows are append-only.
/// </summary>
public sealed class LedgerCheckpoint : ITenantOwned, IAppendOnly
{
    public const int HashSize = 32;

    private LedgerCheckpoint()
    {
    }

    public long Id { get; private set; }

    public Guid LicenseId { get; private set; }

    public Guid ClientId { get; set; }

    /// <summary>Id of the newest ledger row covered by this checkpoint.</summary>
    public long LastEntryId { get; private set; }

    /// <summary>How many ledger rows the license had up to and including <see cref="LastEntryId"/>.</summary>
    public long EntryCount { get; private set; }

    public byte[] HeadHash { get; private set; } = [];

    public int BalanceAfter { get; private set; }

    /// <summary>Which key produced the MAC (the master key id), so a key rotation can be told apart from tampering.</summary>
    public string KeyId { get; private set; } = string.Empty;

    public byte[] Mac { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public static LedgerCheckpoint Create(
        Guid licenseId, Guid clientId, long lastEntryId, long entryCount, byte[] headHash, int balanceAfter, string keyId, DateTime now, Func<byte[], byte[]> sign)
    {
        var checkpoint = new LedgerCheckpoint
        {
            LicenseId = licenseId,
            ClientId = clientId,
            LastEntryId = lastEntryId,
            EntryCount = entryCount,
            HeadHash = headHash,
            BalanceAfter = balanceAfter,
            KeyId = keyId,
            CreatedAt = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc), // the column keeps milliseconds only
        };
        checkpoint.Mac = sign(checkpoint.CanonicalBytes());
        return checkpoint;
    }

    /// <summary>The exact bytes the MAC covers: fixed order, invariant formats, every identifying field including the license and client.</summary>
    public byte[] CanonicalBytes()
    {
        var text = string.Join(
            '|',
            "nxv-ledger-checkpoint-v1",
            LicenseId.ToString("N"),
            ClientId.ToString("N"),
            LastEntryId.ToString(CultureInfo.InvariantCulture),
            EntryCount.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(HeadHash),
            BalanceAfter.ToString(CultureInfo.InvariantCulture),
            KeyId,
            CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture));
        return Encoding.UTF8.GetBytes(text);
    }
}

/// <summary>
/// One persistent ledger finding, remembered so that a break that stays broken is reported to the audit trail ONCE (per license and
/// first broken row) instead of every night. The operator alert (Critical log event 7001) fires when the record is opened and then
/// only as an occasional reminder; quiet runs log a Warning (event 7003).
/// </summary>
public sealed class LedgerBreakRecord : Entity, ITenantOwned
{
    private LedgerBreakRecord()
    {
    }

    public Guid ClientId { get; set; }

    public Guid LicenseId { get; private set; }

    /// <summary>"row:{entryId}", "balance", "checkpoint:{lastEntryId}" or "unreadable": what is broken.</summary>
    public string BreakKey { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public DateTime FirstSeenAt { get; private set; }

    public DateTime LastSeenAt { get; private set; }

    public int TimesSeen { get; private set; }

    /// <summary>Set when a later run verified the license clean again.</summary>
    public DateTime? ClearedAt { get; private set; }

    /// <summary>When the operator was alerted about this break (set when the record is opened; a persistent break is not re-alerted every run).</summary>
    public DateTime? AlertedAt { get; private set; }

    /// <summary>When the operator was last reminded that the break is still unresolved.</summary>
    public DateTime? LastReminderAt { get; private set; }

    public void Seen(DateTime now)
    {
        LastSeenAt = now;
        TimesSeen++;
    }

    /// <summary>
    /// True when the operator should be told again: never when <paramref name="reminderAfter"/> is zero (alert once), otherwise once
    /// that long has passed since the last alert or reminder.
    /// </summary>
    public bool ReminderDue(DateTime now, TimeSpan reminderAfter) =>
        reminderAfter > TimeSpan.Zero && now - (LastReminderAt ?? AlertedAt ?? FirstSeenAt) >= reminderAfter;

    /// <summary>Records that the operator was alerted (first call) or reminded (later calls).</summary>
    public void MarkAlerted(DateTime now)
    {
        if (AlertedAt is null)
        {
            AlertedAt = now;
        }
        else
        {
            LastReminderAt = now;
        }
    }

    public void Clear(DateTime now) => ClearedAt = now;

    public static LedgerBreakRecord Open(Guid licenseId, Guid clientId, string breakKey, string reason, DateTime now) => new()
    {
        LicenseId = licenseId,
        ClientId = clientId,
        BreakKey = breakKey,
        Reason = reason.Length > 400 ? reason[..400] : reason,
        FirstSeenAt = now,
        LastSeenAt = now,
        TimesSeen = 1,
        AlertedAt = now,
    };
}

public enum LedgerRunStatus
{
    Running = 0,
    Completed,
    Failed,
}

/// <summary>One ledger verification run (on demand or nightly): its progress and, once finished, its result. Platform-level data.</summary>
public sealed class LedgerVerificationRun : Entity
{
    private LedgerVerificationRun()
    {
    }

    public LedgerRunStatus Status { get; private set; }

    /// <summary>"manual" or "nightly".</summary>
    public string Trigger { get; private set; } = string.Empty;

    public Guid? RequestedBy { get; private set; }

    /// <summary>When set, only this license was verified.</summary>
    public Guid? LicenseId { get; private set; }

    public DateTime StartedAt { get; private set; }

    public DateTime? FinishedAt { get; private set; }

    public int LicensesChecked { get; private set; }

    public long EntriesChecked { get; private set; }

    public int BrokenLicenses { get; private set; }

    /// <summary>JSON of the breaks (bounded), for the status endpoint.</summary>
    public string? BreaksJson { get; private set; }

    public string? Error { get; private set; }

    public static LedgerVerificationRun Start(string trigger, Guid? requestedBy, Guid? licenseId, DateTime now) => new()
    {
        Status = LedgerRunStatus.Running,
        Trigger = trigger,
        RequestedBy = requestedBy,
        LicenseId = licenseId,
        StartedAt = now,
    };

    public void Complete(int licenses, long entries, int broken, string breaksJson, DateTime now)
    {
        Status = LedgerRunStatus.Completed;
        LicensesChecked = licenses;
        EntriesChecked = entries;
        BrokenLicenses = broken;
        BreaksJson = breaksJson;
        FinishedAt = now;
    }

    public void Fail(string error, DateTime now)
    {
        Status = LedgerRunStatus.Failed;
        Error = error.Length > 400 ? error[..400] : error;
        FinishedAt = now;
    }
}
