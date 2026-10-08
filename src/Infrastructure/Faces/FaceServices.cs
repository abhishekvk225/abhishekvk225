using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Faces;
using NexaVerify.Domain.Faces;
using NexaVerify.Infrastructure.Persistence;

namespace NexaVerify.Infrastructure.Faces;

public sealed class FaceEngineOptions
{
    public const string Section = "FaceEngine";

    /// <summary>Which provider to use. Only "mock" ships today; real providers register under their own name.</summary>
    public string Provider { get; set; } = "mock";

    /// <summary>The mock engine recognises nothing; it is refused in Production unless this is explicitly set (demo environments).</summary>
    public bool AllowMockInProduction { get; set; }
}

public sealed class FaceEngineOptionsValidator : IValidateOptions<FaceEngineOptions>
{
    private readonly IHostEnvironment _environment;

    public FaceEngineOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, FaceEngineOptions options)
    {
        if (!string.Equals(options.Provider, "mock", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail($"Unknown face engine provider '{options.Provider}'. Available: mock.");
        }

        return _environment.IsProduction() && !options.AllowMockInProduction
            ? ValidateOptionsResult.Fail("The mock face engine cannot run in Production. Configure a real provider (FaceEngine:Provider), or set FaceEngine:AllowMockInProduction for a demo deployment.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>Embeddings are stored encrypted under the owning client's key and bound to a purpose label.</summary>
internal sealed class EmbeddingCodec : IEmbeddingCodec
{
    private const string Purpose = "face-template";

    private readonly IClientEncryption _encryption;

    public EmbeddingCodec(IClientEncryption encryption)
    {
        _encryption = encryption;
    }

    public Task<byte[]> EncryptAsync(Guid clientId, float[] embedding, CancellationToken cancellationToken) =>
        _encryption.EncryptAsync(clientId, MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray(), Purpose, cancellationToken);

    public async Task<float[]> DecryptAsync(Guid clientId, byte[] payload, CancellationToken cancellationToken)
    {
        var bytes = await _encryption.DecryptAsync(clientId, payload, Purpose, cancellationToken);
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
        {
            throw new InvalidOperationException("A stored face template is corrupt.");
        }

        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }
}

/// <summary>
/// Per-client in-memory cache of decrypted embeddings, shared by all requests on this node. A short TTL bounds staleness across
/// nodes; a per-client generation counter stops a load that started before an invalidation from being cached afterwards, and a
/// per-client gate makes concurrent cold misses share one load instead of each decrypting the whole gallery.
/// </summary>
public sealed class TemplateIndexStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    // Size is counted in KiB of embedding data, so the limit bounds memory (≈ 1 GiB) rather than the number of templates.
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1_048_576 });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _clients = new();
    private readonly ConcurrentDictionary<Guid, int> _generations = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public SemaphoreSlim GateFor(Guid clientId) => _gates.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));

    public int Generation(Guid clientId) => _generations.GetValueOrDefault(clientId);

    public bool TryGet(Guid clientId, string provider, string model, out IReadOnlyList<IndexedTemplate> templates) =>
        _cache.TryGetValue(Key(clientId, provider, model), out templates!);

    /// <summary>Caches the gallery unless an invalidation happened since <paramref name="generation"/> was read.</summary>
    public void Set(Guid clientId, string provider, string model, IReadOnlyList<IndexedTemplate> templates, int generation)
    {
        if (Generation(clientId) != generation)
        {
            return;
        }

        var cts = _clients.GetOrAdd(clientId, _ => new CancellationTokenSource());
        var kib = templates.Count == 0 ? 1 : Math.Max(1, (int)(templates.Sum(t => (long)t.Embedding.Length * sizeof(float)) / 1024));
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = kib };
        options.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(cts.Token));
        _cache.Set(Key(clientId, provider, model), templates, options);
    }

    public void Invalidate(Guid clientId)
    {
        _generations.AddOrUpdate(clientId, 1, (_, g) => g + 1);
        if (_clients.TryRemove(clientId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static string Key(Guid clientId, string provider, string model) => $"{clientId:N}|{provider}|{model}";
}

internal sealed class TemplateIndex : ITemplateIndex
{
    private readonly AppDbContext _db;
    private readonly TemplateIndexStore _store;
    private readonly IEmbeddingCodec _codec;
    private readonly Microsoft.Extensions.Logging.ILogger<TemplateIndex> _logger;

    public TemplateIndex(AppDbContext db, TemplateIndexStore store, IEmbeddingCodec codec, Microsoft.Extensions.Logging.ILogger<TemplateIndex> logger)
    {
        _logger = logger;
        _db = db;
        _store = store;
        _codec = codec;
    }

    public async Task<IReadOnlyList<IndexedTemplate>> GetAsync(Guid clientId, string provider, string modelVersion, CancellationToken cancellationToken)
    {
        if (_store.TryGet(clientId, provider, modelVersion, out var cached))
        {
            return cached;
        }

        var gate = _store.GateFor(clientId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_store.TryGet(clientId, provider, modelVersion, out cached))
            {
                return cached; // another request loaded it while this one waited
            }

            var generation = _store.Generation(clientId);
            var rows = await _db.FaceTemplates.AsNoTracking()
                .Where(t => t.ClientId == clientId && t.Provider == provider && t.ModelVersion == modelVersion && t.Status == TemplateStatus.Active
                    && _db.FaceProfiles.Any(p => p.Id == t.ProfileId && p.Status == FaceProfileStatus.Active))
                .Select(t => new { t.Id, t.ProfileId, t.EmbeddingEnc })
                .ToListAsync(cancellationToken);

            var loaded = new List<IndexedTemplate>(rows.Count);
            foreach (var row in rows)
            {
                try
                {
                    loaded.Add(new IndexedTemplate(row.Id, row.ProfileId, await _codec.DecryptAsync(clientId, row.EmbeddingEnc, cancellationToken)));
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException)
                {
                    // One unreadable template must not take identification down for the whole client; it is reported and skipped.
                    _logger.LogError(ex, "Face template {TemplateId} of client {ClientId} could not be decrypted and was skipped", row.Id, clientId);
                }
            }

            _store.Set(clientId, provider, modelVersion, loaded, generation);
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid clientId) => _store.Invalidate(clientId);
}
