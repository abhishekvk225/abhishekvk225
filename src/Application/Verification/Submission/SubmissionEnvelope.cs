using System.Text.Json;
using System.Text.Json.Serialization;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification.Abstractions;

namespace NexaVerify.Application.Verification.Submission;

/// <summary>
/// What is kept (encrypted) for a request in <c>verification.Results.DataEnc</c>: the fields the caller submitted (inputs of the
/// comparison and of the provider call) and, once extraction ran, the extracted data. Both are personal data; the envelope is only ever
/// stored sealed with the owning client's key and is erased with the result.
/// </summary>
public sealed record SubmissionEnvelope(IReadOnlyDictionary<string, string?> Submitted, ExtractedData? Extracted)
{
    public const int CurrentVersion = 1;

    public override string ToString() => nameof(SubmissionEnvelope);
}

/// <summary>Seals and opens <see cref="SubmissionEnvelope"/>s under the client's data key (purpose bound to the request).</summary>
public sealed class VerificationEnvelopeCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 16,
    };

    private readonly IClientEncryption _encryption;

    public VerificationEnvelopeCodec(IClientEncryption encryption)
    {
        _encryption = encryption;
    }

    public static string PurposeFor(Guid requestId) => $"verification-result:{requestId:N}";

    public async Task<byte[]> SealAsync(Guid clientId, Guid requestId, SubmissionEnvelope envelope, CancellationToken cancellationToken)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Wire(envelope.Submitted.ToDictionary(kv => kv.Key, kv => kv.Value), envelope.Extracted), Json);
        try
        {
            return await _encryption.EncryptAsync(clientId, plain, PurposeFor(requestId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>Null when the payload cannot be opened (wrong key / tampered / unknown shape): the caller treats it as "no data".</summary>
    public async Task<SubmissionEnvelope?> OpenAsync(Guid clientId, Guid requestId, byte[] payload, CancellationToken cancellationToken)
    {
        byte[]? plain = null;
        try
        {
            plain = await _encryption.DecryptAsync(clientId, payload, PurposeFor(requestId), cancellationToken).ConfigureAwait(false);
            var wire = JsonSerializer.Deserialize<Wire>(plain, Json);
            return wire is null ? null : new SubmissionEnvelope(wire.Submitted ?? new Dictionary<string, string?>(), wire.Extracted);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
        finally
        {
            if (plain is not null)
            {
                Array.Clear(plain);
            }
        }
    }

    private sealed record Wire(Dictionary<string, string?>? Submitted, ExtractedData? Extracted);
}
