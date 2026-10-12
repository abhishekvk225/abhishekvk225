using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.Verification.Abstractions;

// Persistence ports of M14 (implemented in Infrastructure/Persistence and Infrastructure/Platform; schema `verification`, docs/02).
// Database-agnostic and deliberately small. Scope rules (enforced by the EF filter, the SaveChanges write guard and SQL row-level security):
//   * "tenant scope"   = the caller is inside BeginTenant(clientId) / an authenticated client request. Reads and writes see that client only.
//   * "platform scope" = BeginPlatform(...). Can read/write VerificationRequests, VerificationEvents and VerificationDownloadGrants of every tenant,
//                        but NEVER documents, blobs, results or checks (strict tenant tables): those need the owning tenant's scope.
// Methods that only stage changes (`Add`, entity mutation) are persisted by IUnitOfWork.SaveChangesAsync, like every other repository.

public enum VerificationListSort
{
    CreatedDesc = 0,
    CreatedAsc,
    CompletedDesc,
}

/// <summary>History filter. All members optional; <see cref="Search"/> is a full request id, a request-id prefix, or the start of the caller's external reference.</summary>
public sealed record VerificationListFilter(
    VerificationType? Type = null, VerificationStatus? Status = null, bool? ReviewRequired = null, ExternalState? ExternalState = null,
    DateTime? From = null, DateTime? To = null, string? Search = null, VerificationListSort Sort = VerificationListSort.CreatedDesc);

/// <summary>One history row. <see cref="IdentifierMasked"/> is only present in tenant scope (it lives in a strict table) and never for purged results.</summary>
public sealed record VerificationListRow(
    Guid Id, VerificationType Type, VerificationStatus Status, bool ReviewRequired, ExternalState ExternalState, AssuranceLevel Assurance,
    string? IdentifierMasked, string? ExternalRef, DateTime CreatedAt, DateTime? CompletedAt, int CreditsCharged, int ProviderCreditsCharged);

public sealed record VerificationCreateOutcome(VerificationRequest Request, bool Created);

/// <summary>Tenant-scope access to <c>verification.Requests</c> (+ their events). Reads are tenant-filtered; an id of another tenant is "not found".</summary>
public interface IVerificationRepository
{
    /// <summary>Stages a new request (and the creation event it carries). Saved by <c>IUnitOfWork</c>.</summary>
    void Add(VerificationRequest request);

    /// <summary>Tracked load for mutation (state machine, charges). Null when it does not exist for this tenant.</summary>
    Task<VerificationRequest?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<VerificationRequest?> GetReadOnlyAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The existing request of this tenant with that server-derived idempotency key (no tracking), or null.</summary>
    Task<VerificationRequest?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Idempotent create: inserts the request, its events and (optionally) the encrypted document in ONE transaction. When the unique
    /// <c>(ClientId, IdempotencyKey)</c> index rejects it (a concurrent identical submit) nothing is stored and the winner is returned with
    /// <c>Created = false</c>. Must not be called inside an outer transaction (it owns the transaction and clears the tracker on a conflict).
    /// </summary>
    Task<VerificationCreateOutcome> CreateIdempotentAsync(
        VerificationRequest request, VerificationDocument? document, VerificationDocumentBlob? blob, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the pending changes with optimistic concurrency (<c>RowVersion</c>). Returns false when another writer changed the request in
    /// the meantime (the tracker is cleared; reload and decide); true on success.
    /// </summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken);

    /// <summary>Queued + Processing requests of this tenant (the <c>MaxQueuedPerClient</c> cap).</summary>
    Task<int> CountOpenAsync(CancellationToken cancellationToken);

    /// <summary>The newest request of this tenant with the same document hash (duplicate-upload hint), or null.</summary>
    Task<Guid?> FindLatestByDocumentHashAsync(byte[] sha256, CancellationToken cancellationToken);

    /// <summary>Filtered, paged history (no tracking; <paramref name="take"/> is clamped to 1..500).</summary>
    Task<(IReadOnlyList<VerificationListRow> Items, int Total)> ListAsync(VerificationListFilter filter, int skip, int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<VerificationEvent>> ListEventsAsync(Guid requestId, int take, CancellationToken cancellationToken);
}

/// <summary>An encrypted document, returned only for the request that owns it.</summary>
public sealed record DocumentCiphertext(Guid DocumentId, Guid RequestId, byte[] Content);

/// <summary>Tenant-scope access to the strict tables <c>Documents</c> / <c>DocumentBlobs</c>. Ciphertext is only ever returned for a (tenant, request) pair.</summary>
public interface IVerificationDocumentStore
{
    void Add(VerificationDocument document, VerificationDocumentBlob blob);

    /// <summary>Tracked metadata of the request's document, or null.</summary>
    Task<VerificationDocument?> GetAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>The ciphertext of the request's document; null when there is none or it was purged.</summary>
    Task<DocumentCiphertext?> GetCiphertextAsync(Guid requestId, CancellationToken cancellationToken);
}

/// <summary>Tenant-scope access to the strict tables <c>Results</c> / <c>Checks</c>.</summary>
public interface IVerificationResultStore
{
    Task<VerificationResult?> GetResultAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes any existing result of the request immediately and stages the new one (saved by the next <c>SaveChanges</c>). Call it inside the
    /// stage transaction (<c>IUnitOfWork.ExecuteInTransactionAsync</c>) so the delete and the insert commit together.
    /// </summary>
    Task ReplaceResultAsync(VerificationResult result, CancellationToken cancellationToken);

    Task<IReadOnlyList<VerificationCheck>> ListChecksAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Replaces the checks of the given stages (delete now, insert on the next save): a re-run of a stage never duplicates its checks.</summary>
    Task ReplaceChecksAsync(Guid requestId, IReadOnlyCollection<CheckStage> stages, IEnumerable<VerificationCheck> checks, CancellationToken cancellationToken);

    /// <summary>How many results of this tenant carry the same keyed identifier hash (dedupe / velocity), optionally excluding one request.</summary>
    Task<int> CountByIdentifierHashAsync(byte[] identifierHash, Guid? excludingRequestId, CancellationToken cancellationToken);
}

/// <summary>A request handed to a worker by <see cref="IVerificationQueue.ClaimDueAsync"/>. Work on it inside <c>BeginTenant(ClientId)</c>.</summary>
public sealed record ClaimedVerification(Guid RequestId, Guid ClientId, VerificationType Type, int Attempts, Guid LeaseToken, DateTime LeaseUntil);

public sealed record LeaseSweepResult(int Requeued, int Failed);

/// <summary>
/// The work queue over <c>verification.Requests</c>. Every method is a single set-based statement and is safe to run from many nodes
/// at once (UPDLOCK/READPAST, like the e-mail outbox). Call it in <b>platform scope</b> (it works across tenants; it never touches PII tables).
/// Each state change also appends the matching <c>Events</c> row in the same statement.
/// </summary>
public interface IVerificationQueue
{
    /// <summary>
    /// Claims up to <paramref name="batch"/> due <c>Queued</c> requests (oldest first, at most <paramref name="perClientLimit"/> per tenant so one
    /// tenant cannot starve the rest): Queued -> Processing, lease until <c>now + lease</c> under <paramref name="leaseToken"/>, attempt counted at claim.
    /// Two concurrent claimers never receive the same request.
    /// </summary>
    Task<IReadOnlyList<ClaimedVerification>> ClaimDueAsync(int batch, int perClientLimit, DateTime now, TimeSpan lease, Guid leaseToken, CancellationToken cancellationToken);

    /// <summary>Heartbeat: extends the lease only while <paramref name="leaseToken"/> still owns the request. False when the lease was lost.</summary>
    Task<bool> ExtendLeaseAsync(Guid requestId, Guid leaseToken, DateTime leaseUntil, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Recovers requests whose lease expired (crashed node): back to Queued (due now) while attempts &lt; <paramref name="maxAttempts"/>, otherwise Failed
    /// with <c>PROCESSING_TIMEOUT</c> (a poison document cannot loop forever; nothing was charged). At most <paramref name="take"/> per call.
    /// </summary>
    Task<LeaseSweepResult> RequeueExpiredLeasesAsync(DateTime now, int maxAttempts, int take, CancellationToken cancellationToken);

    /// <summary>Graceful shutdown: gives back everything still leased under <paramref name="leaseToken"/> without counting the attempt.</summary>
    Task<int> ReleaseHeldAsync(Guid leaseToken, DateTime notBefore, DateTime now, CancellationToken cancellationToken);

    /// <summary>Queued requests older than <paramref name="queuedBefore"/> become Expired (never charged). At most <paramref name="take"/> per call.</summary>
    Task<int> ExpireStaleQueuedAsync(DateTime queuedBefore, DateTime now, int take, CancellationToken cancellationToken);
}

public enum GrantConsumeStatus
{
    Consumed = 0,

    /// <summary>No grant with that token for this tenant (HTTP 404; also the answer for another tenant's token).</summary>
    NotFound,

    /// <summary>Expired or already used (HTTP 410).</summary>
    ExpiredOrUsed,

    /// <summary>The grant was issued to another principal; it is NOT consumed (HTTP 404/403 at the caller's choice).</summary>
    NotOwner,
}

public sealed record GrantConsumeResult(GrantConsumeStatus Status, Guid? RequestId, DownloadKind? Kind, string? Format);

/// <summary>Single-use download grants (tenant scope). Only the SHA-256 of the token is ever stored.</summary>
public interface IVerificationDownloadGrants
{
    void Add(VerificationDownloadGrant grant);

    /// <summary>
    /// Consumes the grant with ONE conditional <c>UPDATE ... WHERE UsedAt IS NULL AND ExpiresAt &gt; now AND PrincipalId = @principal</c>: exactly one
    /// of any number of concurrent callers gets <see cref="GrantConsumeStatus.Consumed"/>.
    /// </summary>
    Task<GrantConsumeResult> ConsumeAsync(byte[] tokenHash, Guid principalId, DateTime now, CancellationToken cancellationToken);
}

public sealed record DueVerification(Guid RequestId, Guid ClientId);

/// <summary>
/// Retention and erasure primitives (the sweeper job itself belongs to Backend). Purges bypass the entity tracker (set-based deletes), so run them
/// in the OWNING tenant's scope; only <see cref="ListRequestsDueAsync"/>, <see cref="PurgeExpiredGrantsAsync"/> and <see cref="BlankIpAddressesAsync"/>
/// may also run in platform scope (they touch tenant-or-platform tables only).
/// </summary>
public interface IVerificationRetention
{
    /// <summary>Requests whose <c>ExpiresAt</c> has passed and whose data is not yet erased (oldest first, at most <paramref name="take"/>).</summary>
    Task<IReadOnlyList<DueVerification>> ListRequestsDueAsync(DateTime now, int take, CancellationToken cancellationToken);

    /// <summary>Tenant scope: requests whose document passed <c>PurgeAfter</c> and is not purged.</summary>
    Task<IReadOnlyList<Guid>> ListDocumentsDueAsync(DateTime now, int take, CancellationToken cancellationToken);

    /// <summary>Tenant scope: requests whose result passed <c>RetainUntil</c>.</summary>
    Task<IReadOnlyList<Guid>> ListResultsDueAsync(DateTime now, int take, CancellationToken cancellationToken);

    /// <summary>Tenant scope: deletes the document ciphertext and marks the document purged. True when something was purged.</summary>
    Task<bool> PurgeDocumentAsync(Guid requestId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Tenant scope: deletes the result and the checks. True when something was deleted.</summary>
    Task<bool> PurgeResultAsync(Guid requestId, CancellationToken cancellationToken);

    /// <summary>Tenant scope: erasure (<c>DELETE .../data</c> or expiry): document + result + checks, then <c>Requests.PurgedAt</c>. Idempotent.</summary>
    Task<bool> EraseAsync(Guid requestId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Deletes grants that expired before <paramref name="expiredBefore"/>. Returns the number removed (at most <paramref name="take"/>).</summary>
    Task<int> PurgeExpiredGrantsAsync(DateTime expiredBefore, int take, CancellationToken cancellationToken);

    /// <summary>Blanks <c>IpAddress</c> of requests created before <paramref name="createdBefore"/> (personal metadata window). At most <paramref name="take"/>.</summary>
    Task<int> BlankIpAddressesAsync(DateTime createdBefore, int take, CancellationToken cancellationToken);
}

public sealed record VerificationTimePoint(DateOnly Day, VerificationType Type, int Total, int Completed, int ReviewRequired, int Failed, long CreditsCharged);

/// <summary>Provider outcomes for one provider id within a period.</summary>
public sealed record ProviderOutcomeRow(string ProviderId, ProviderOutcomeCountsDto Counts);

/// <summary>
/// Metadata-only queries for the admin portal and tenant dashboards. They read <c>Requests</c> + <c>Events</c> only (no strict table, no join to
/// personal data). In platform scope they span tenants (optionally narrowed by <c>ClientId</c>); in tenant scope the tenant filter applies.
/// </summary>
public interface IVerificationAdminQueries
{
    /// <summary>Volumes by type/status, review count, failures by error code, provider outcomes, credits by stage, p50/p95 processing seconds, queue depth and oldest queued age.</summary>
    Task<VerificationStatsDto> GetStatsAsync(VerificationStatsQuery query, DateTime now, CancellationToken cancellationToken);

    /// <summary>Per UTC day and type: total, completed, review-required, failed and credits charged (success/failure/credit consumption by type and period).</summary>
    Task<IReadOnlyList<VerificationTimePoint>> GetTimeSeriesAsync(VerificationStatsQuery query, CancellationToken cancellationToken);

    /// <summary>Provider answers (Verified, NotVerified, Inconclusive, ProviderError, Timeout) per provider id since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<ProviderOutcomeRow>> GetProviderOutcomesAsync(DateTime since, CancellationToken cancellationToken);

    Task<(IReadOnlyList<AdminVerificationItemDto> Items, int Total)> ListForClientAsync(Guid clientId, AdminVerificationListQuery query, CancellationToken cancellationToken);

    Task<AdminVerificationDetailDto?> GetDetailAsync(Guid clientId, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>Write side of the global module switch (the read side is <c>IVerificationModuleSettingReader</c>). Not tenant-owned: no ClientId.</summary>
public interface IVerificationModuleSettings
{
    Task<IReadOnlyList<VerificationModuleSetting>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Tracked (carries the <c>RowVersion</c> for optimistic concurrency), or null when the module has no row.</summary>
    Task<VerificationModuleSetting?> GetAsync(string moduleKey, CancellationToken cancellationToken);

    void Add(VerificationModuleSetting setting);
}
