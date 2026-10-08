using NexaVerify.Application.Common;

namespace NexaVerify.Application.Faces;

/// <summary>An image that passed validation and was re-encoded (metadata stripped, size bounded).</summary>
public sealed record PreparedImage(byte[] Jpeg, int Width, int Height, byte[] Sha256);

public sealed record DetectedFace(int X, int Y, int Width, int Height, decimal Quality);

/// <summary>Validates untrusted upload bytes (magic bytes, size, pixel count) and normalises them. Pure CPU work, no I/O.</summary>
public interface IImageProcessor
{
    Result<PreparedImage> Prepare(byte[] data);
}

/// <summary>The face provider contract. Swapping the implementation never touches business code.</summary>
public interface IFaceEngine
{
    string Provider { get; }

    string ModelVersion { get; }

    int Dimensions { get; }

    Task<IReadOnlyList<DetectedFace>> DetectAsync(PreparedImage image, CancellationToken cancellationToken);

    Task<float[]> ExtractAsync(PreparedImage image, DetectedFace face, CancellationToken cancellationToken);

    /// <summary>Similarity of two embeddings of this provider, 0 (different) to 1 (identical).</summary>
    double Similarity(float[] a, float[] b);
}

/// <summary>The provider could not answer (outage, timeout, model failure). Never billed.</summary>
public sealed class FaceProviderException : Exception
{
    public FaceProviderException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed record IndexedTemplate(Guid TemplateId, Guid ProfileId, float[] Embedding);

/// <summary>Decrypted embeddings of a client's active templates for 1:N search, cached in memory per client.</summary>
public interface ITemplateIndex
{
    Task<IReadOnlyList<IndexedTemplate>> GetAsync(Guid clientId, string provider, string modelVersion, CancellationToken cancellationToken);

    /// <summary>Call after any template is added or removed.</summary>
    void Invalidate(Guid clientId);
}

public interface IEmbeddingCodec
{
    Task<byte[]> EncryptAsync(Guid clientId, float[] embedding, CancellationToken cancellationToken);

    Task<float[]> DecryptAsync(Guid clientId, byte[] payload, CancellationToken cancellationToken);
}

/// <summary>Pure scoring helpers (unit-testable without a database or an engine).</summary>
public static class Scoring
{
    public static IReadOnlyList<(IndexedTemplate Template, double Score)> Rank(
        IFaceEngine engine, float[] probe, IEnumerable<IndexedTemplate> candidates)
    {
        // Best template per profile, highest first.
        return candidates
            .Select(t => (Template: t, Score: engine.Similarity(probe, t.Embedding)))
            .GroupBy(x => x.Template.ProfileId)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .ToList();
    }
}
