using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Faces;

/// <summary>A face embedding of one profile, produced by one provider/model and encrypted under the client's key. Never leaves the API.</summary>
public sealed class FaceTemplate : Entity, IStrictTenantOwned
{
    private FaceTemplate()
    {
    }

    public Guid ClientId { get; set; }

    public Guid ProfileId { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string ModelVersion { get; private set; } = string.Empty;

    public int Dimensions { get; private set; }

    public byte[] EmbeddingEnc { get; private set; } = [];

    public decimal QualityScore { get; private set; }

    public byte[] ImageSha256 { get; private set; } = [];

    public TemplateStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static FaceTemplate Create(
        Guid clientId, Guid profileId, string provider, string modelVersion, int dimensions, byte[] embeddingEnc, decimal quality, byte[] imageSha256, DateTime now) =>
        new()
        {
            ClientId = clientId,
            ProfileId = profileId,
            Provider = provider,
            ModelVersion = modelVersion,
            Dimensions = dimensions,
            EmbeddingEnc = embeddingEnc,
            QualityScore = Math.Round(quality, 4),
            ImageSha256 = imageSha256,
            Status = TemplateStatus.Active,
            CreatedAt = now,
        };
}
