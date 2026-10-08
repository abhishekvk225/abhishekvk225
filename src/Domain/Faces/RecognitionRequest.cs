using NexaVerify.Domain.Common;
using NexaVerify.Domain.Licensing;

namespace NexaVerify.Domain.Faces;

/// <summary>One recognition attempt: what was asked, the stable outcome, what it cost. Written once, when the attempt finishes.</summary>
public sealed class RecognitionRequest : Entity, IStrictTenantOwned
{
    private readonly List<MatchResult> _matches = [];

    private RecognitionRequest()
    {
    }

    public Guid ClientId { get; set; }

    public MeteredOperation Operation { get; private set; }

    public RecognitionSource Source { get; private set; }

    public Guid? ApiKeyId { get; private set; }

    public Guid? UserId { get; private set; }

    /// <summary>Verify: the profile compared against (no foreign key, so erasing a profile never blocks on history).</summary>
    public Guid? TargetProfileId { get; private set; }

    public RecognitionStatus Status { get; private set; }

    public RecognitionOutcome Outcome { get; private set; }

    public string? ErrorCode { get; private set; }

    public decimal ThresholdUsed { get; private set; }

    public decimal? BestScore { get; private set; }

    public int CandidateCount { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string ModelVersion { get; private set; } = string.Empty;

    public byte[] InputImageSha256 { get; private set; } = [];

    public int LatencyMs { get; private set; }

    public int CreditsCharged { get; private set; }

    public int? BalanceAfter { get; private set; }

    public long? LicenseTransactionId { get; private set; }

    /// <summary>Server-derived (client key + operation + image fingerprint), so a key cannot be reused to skip billing for other requests.</summary>
    public string? IdempotencyKey { get; private set; }

    public string? IpAddress { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public IReadOnlyList<MatchResult> Matches => _matches;

    public static RecognitionRequest Create(
        Guid id, Guid clientId, MeteredOperation operation, RecognitionSource source, Guid? actorId, Guid? targetProfileId,
        RecognitionOutcome outcome, string? errorCode, decimal threshold, decimal? bestScore, int candidateCount,
        string provider, string modelVersion, byte[] imageSha256, int latencyMs, string? idempotencyKey, string? ipAddress, string? correlationId, DateTime now)
    {
        var request = new RecognitionRequest
        {
            Id = id,
            ClientId = clientId,
            Operation = operation,
            Source = source,
            ApiKeyId = source == RecognitionSource.Api ? actorId : null,
            UserId = source == RecognitionSource.Portal ? actorId : null,
            TargetProfileId = targetProfileId,
            Status = outcome is RecognitionOutcome.Enrolled or RecognitionOutcome.Matched or RecognitionOutcome.NoMatch
                ? RecognitionStatus.Completed
                : RecognitionStatus.Failed,
            Outcome = outcome,
            ErrorCode = errorCode,
            ThresholdUsed = threshold,
            BestScore = bestScore is null ? null : Math.Round(bestScore.Value, 4),
            CandidateCount = candidateCount,
            Provider = provider,
            ModelVersion = modelVersion,
            InputImageSha256 = imageSha256,
            LatencyMs = latencyMs,
            IdempotencyKey = idempotencyKey,
            IpAddress = ipAddress is { Length: > 45 } ip ? ip[..45] : ipAddress,
            CorrelationId = correlationId is { Length: > 64 } c ? c[..64] : correlationId,
            CreatedAt = now,
        };
        return request;
    }

    public void AddMatch(Guid profileId, Guid templateId, decimal score, bool isMatch) =>
        _matches.Add(MatchResult.Create(Id, ClientId, (byte)(_matches.Count + 1), profileId, templateId, score, isMatch));

    public void RecordCharge(int credits, int? balanceAfter, long? transactionId)
    {
        CreditsCharged = credits;
        BalanceAfter = balanceAfter;
        LicenseTransactionId = transactionId;
    }
}

/// <summary>A candidate returned by a verification or identification, ranked by score.</summary>
public sealed class MatchResult : IStrictTenantOwned
{
    private MatchResult()
    {
    }

    public Guid RequestId { get; private set; }

    public byte Rank { get; private set; }

    public Guid ClientId { get; set; }

    public Guid ProfileId { get; private set; }

    public Guid TemplateId { get; private set; }

    public decimal Score { get; private set; }

    public bool IsMatch { get; private set; }

    internal static MatchResult Create(Guid requestId, Guid clientId, byte rank, Guid profileId, Guid templateId, decimal score, bool isMatch) =>
        new() { RequestId = requestId, ClientId = clientId, Rank = rank, ProfileId = profileId, TemplateId = templateId, Score = Math.Round(score, 4), IsMatch = isMatch };
}
