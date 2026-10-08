namespace NexaVerify.Contracts.Faces;

/// <summary>Form fields accompanying the uploaded image when registering a face.</summary>
public sealed record EnrollFaceRequest(string ExternalRef, string? DisplayName, string? Metadata, string ConsentReference);

/// <summary>Identify the person to compare against by profile id or by the client's own reference.</summary>
public sealed record VerifyFaceRequest(Guid? ProfileId, string? ExternalRef);

public sealed record IdentifyFaceRequest(int? TopK);

public sealed record CreditsDto(int Charged, int? Remaining);

public sealed record EnrollFaceResponse(Guid ProfileId, Guid TemplateId, bool ProfileCreated, decimal Quality, string Outcome, Guid RequestId, CreditsDto Credits);

public sealed record VerifyFaceResponse(bool Match, decimal Score, decimal Threshold, string Outcome, Guid ProfileId, Guid RequestId, CreditsDto Credits);

public sealed record FaceMatchDto(Guid ProfileId, string ExternalRef, decimal Score);

public sealed record IdentifyFaceResponse(IReadOnlyList<FaceMatchDto> Matches, decimal? BestScore, decimal Threshold, string Outcome, Guid RequestId, CreditsDto Credits);

public sealed record DetectedFaceDto(int X, int Y, int Width, int Height, decimal Quality);

public sealed record DetectFaceResponse(int FaceCount, IReadOnlyList<DetectedFaceDto> Faces, Guid RequestId, CreditsDto Credits);

public sealed record FaceTemplateDto(Guid Id, string Provider, string ModelVersion, decimal Quality, string Status, DateTime CreatedAt);

/// <summary>A registered person. Embeddings are never part of any response.</summary>
public sealed record FaceProfileDto(
    Guid Id, string ExternalRef, string? DisplayName, string? Metadata, string Status, string ConsentReference, DateTime ConsentRecordedAt,
    DateTime? RetentionUntil, int TemplateCount, IReadOnlyList<FaceTemplateDto> Templates, DateTime CreatedAt, DateTime? UpdatedAt);

public sealed record FaceProfileListItemDto(
    Guid Id, string ExternalRef, string? DisplayName, string Status, int TemplateCount, DateTime? RetentionUntil, DateTime CreatedAt);

public sealed record FaceProfileListQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public string? Search { get; init; }

    public string? Status { get; init; }
}

/// <summary>All fields optional; omitted fields stay unchanged. <c>Status</c> is Active or Disabled.</summary>
public sealed record UpdateFaceProfileRequest(string? DisplayName, string? Metadata, string? Status, DateTime? RetentionUntil);

public sealed record RecognitionRequestDto(
    Guid Id, string Operation, string Source, string Status, string Outcome, string? ErrorCode, decimal Threshold, decimal? BestScore,
    int CandidateCount, string Provider, int LatencyMs, int CreditsCharged, Guid? TargetProfileId, DateTime CreatedAt);

public sealed record RecognitionCandidateDto(int Rank, Guid ProfileId, string? ExternalRef, decimal Score, bool IsMatch);

public sealed record RecognitionRequestDetailDto(RecognitionRequestDto Request, int? BalanceAfter, IReadOnlyList<RecognitionCandidateDto> Candidates);

public sealed record RecognitionHistoryQuery
{
    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 25;

    public string? Operation { get; init; }

    public string? Outcome { get; init; }

    public Guid? ProfileId { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}

public sealed record FaceBalanceDto(int Remaining, DateTime? ExpiresAt, string Status);
