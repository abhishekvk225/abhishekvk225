using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Infrastructure.Verification.Storage;

/// <summary>
/// Encrypted document storage in SQL Server (ADR-19): the ciphertext lives in <c>verification.DocumentBlobs</c> (a strict tenant table, so not
/// even platform scope can read it), sealed with the owning client's data key and bound to the request through the purpose label. There is no
/// file system path, no public URL and no plaintext at rest; the plaintext only exists in memory while a request is being processed.
/// </summary>
public sealed class SqlDocumentStorageService : IDocumentStorageService
{
    private readonly IClientEncryption _encryption;
    private readonly IVerificationDocumentStore _store;
    private readonly IVerificationRetention _retention;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _time;

    public SqlDocumentStorageService(
        IClientEncryption encryption, IVerificationDocumentStore store, IVerificationRetention retention, ITenantContext tenant, TimeProvider time)
    {
        _encryption = encryption;
        _store = store;
        _retention = retention;
        _tenant = tenant;
        _time = time;
    }

    public static string PurposeFor(Guid requestId) => $"verification-document:{requestId:N}";

    public async Task<SealedDocument> SealAsync(
        Guid clientId, Guid requestId, DocumentUpload upload, DocumentInspection inspection, DateTime purgeAfter, CancellationToken cancellationToken)
    {
        var cipher = await _encryption.EncryptAsync(clientId, upload.Content, PurposeFor(requestId), cancellationToken).ConfigureAwait(false);
        var document = VerificationDocument.Create(
            clientId, requestId, inspection.Kind, inspection.ContentType, inspection.Extension, upload.Content.Length, inspection.PageCount, inspection.Sha256, purgeAfter);
        return new SealedDocument(document, VerificationDocumentBlob.Create(clientId, document.Id, cipher));
    }

    public async Task<Result<byte[]>> ReadAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (_tenant.ClientId is not { } clientId)
        {
            return Error.NotFound("The document was not found.");
        }

        var stored = await _store.GetCiphertextAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Error.NotFound("The document was not found.");
        }

        try
        {
            return await _encryption.DecryptAsync(clientId, stored.Content, PurposeFor(requestId), cancellationToken).ConfigureAwait(false);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Wrong key, wrong request or tampered ciphertext: never reveal which.
            return new Error(ErrorCodes.DocumentInvalid, "The stored document could not be read.", ErrorType.UnprocessableEntity);
        }
    }

    public async Task DeleteAsync(Guid requestId, CancellationToken cancellationToken) =>
        _ = await _retention.PurgeDocumentAsync(requestId, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
}
