using NexaVerify.Domain.Common;

namespace NexaVerify.Domain.Verification;

/// <summary>Metadata of the uploaded file. The ciphertext lives in <see cref="VerificationDocumentBlob"/> so list queries never touch LOBs.</summary>
public sealed class VerificationDocument : Entity, IStrictTenantOwned
{
    private VerificationDocument()
    {
    }

    public Guid ClientId { get; set; }

    public Guid RequestId { get; private set; }

    public DocumentKind Kind { get; private set; }

    public string ContentType { get; private set; } = string.Empty;

    public string Extension { get; private set; } = string.Empty;

    public int SizeBytes { get; private set; }

    public int PageCount { get; private set; }

    public byte[] Sha256 { get; private set; } = [];

    public DateTime PurgeAfter { get; private set; }

    public DateTime? PurgedAt { get; private set; }

    public static VerificationDocument Create(
        Guid clientId, Guid requestId, DocumentKind kind, string contentType, string extension, int sizeBytes, int pageCount, byte[] sha256, DateTime purgeAfter) =>
        new()
        {
            ClientId = clientId,
            RequestId = requestId,
            Kind = kind,
            ContentType = contentType,
            Extension = extension,
            SizeBytes = sizeBytes,
            PageCount = pageCount,
            Sha256 = sha256,
            PurgeAfter = purgeAfter,
        };

    public void MarkPurged(DateTime now) => PurgedAt = now;
}

/// <summary>Ciphertext of the document (IClientEncryption, purpose bound to the request). Never holds plaintext.</summary>
public sealed class VerificationDocumentBlob : IStrictTenantOwned
{
    private VerificationDocumentBlob()
    {
    }

    public Guid DocumentId { get; private set; }

    public Guid ClientId { get; set; }

    public byte[] Content { get; private set; } = [];

    public static VerificationDocumentBlob Create(Guid clientId, Guid documentId, byte[] ciphertext) =>
        new() { ClientId = clientId, DocumentId = documentId, Content = ciphertext };
}

/// <summary>Encrypted extracted data plus the only queryable derivatives: a masked identifier and a keyed hash.</summary>
public sealed class VerificationResult : IStrictTenantOwned
{
    private VerificationResult()
    {
    }

    public Guid RequestId { get; private set; }

    public Guid ClientId { get; set; }

    public int SchemaVersion { get; private set; }

    /// <summary>Encrypted JSON of the extracted data (purpose <c>verification-result:{requestId:N}</c>).</summary>
    public byte[] DataEnc { get; private set; } = [];

    public string? IdentifierMasked { get; private set; }

    /// <summary>Keyed MAC of the normalised identifier (dedupe/velocity only). Never returned by any API.</summary>
    public byte[]? IdentifierHash { get; private set; }

    /// <summary>Field-level match outcomes only (no values, no scores).</summary>
    public string FieldMatchesJson { get; private set; } = "[]";

    public DateTime RetainUntil { get; private set; }

    /// <summary>Replaces the field-level outcomes (the provider stage adds its own) without touching the sealed data or the masked identifier.</summary>
    public void SetFieldMatches(string fieldMatchesJson) => FieldMatchesJson = fieldMatchesJson;

    public static VerificationResult Create(
        Guid clientId, Guid requestId, int schemaVersion, byte[] dataEnc, MaskedIdentifier? identifierMasked, byte[]? identifierHash,
        string fieldMatchesJson, DateTime retainUntil) =>
        new()
        {
            ClientId = clientId,
            RequestId = requestId,
            SchemaVersion = schemaVersion,
            DataEnc = dataEnc,
            IdentifierMasked = identifierMasked is { Value.Length: > 0 } m ? m.Value : null,
            IdentifierHash = identifierHash,
            FieldMatchesJson = fieldMatchesJson,
            RetainUntil = retainUntil,
        };
}

/// <summary>One check outcome. <see cref="Message"/> is template text with counts/indices only: never a personal value.</summary>
public sealed class VerificationCheck : IStrictTenantOwned
{
    public const int MaxCodeLength = 60;
    public const int MaxFieldLength = 60;
    public const int MaxMessageLength = 300;

    private VerificationCheck()
    {
    }

    public long Id { get; private set; }

    public Guid ClientId { get; set; }

    public Guid RequestId { get; private set; }

    public int Ordinal { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public CheckStage Stage { get; private set; }

    public CheckOutcome Outcome { get; private set; }

    public CheckSeverity Severity { get; private set; }

    public string? Field { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public static VerificationCheck Create(
        Guid clientId, Guid requestId, int ordinal, string code, CheckStage stage, CheckOutcome outcome, CheckSeverity severity, string? field, string message)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
        {
            throw new DomainException("VERIFICATION_CHECK_INVALID", "A check needs a code of at most 60 characters.");
        }

        return new VerificationCheck
        {
            ClientId = clientId,
            RequestId = requestId,
            Ordinal = ordinal,
            Code = code,
            Stage = stage,
            Outcome = outcome,
            Severity = severity,
            Field = field is { Length: > MaxFieldLength } f ? f[..MaxFieldLength] : field,
            Message = message.Length > MaxMessageLength ? message[..MaxMessageLength] : message,
        };
    }
}

/// <summary>Append-only transition log. No personal data.</summary>
public sealed class VerificationEvent : ITenantOwned, IAppendOnly
{
    private VerificationEvent()
    {
    }

    public long Id { get; private set; }

    public Guid ClientId { get; set; }

    public Guid RequestId { get; private set; }

    /// <summary>Null for the creation event.</summary>
    public VerificationStatus? FromStatus { get; private set; }

    public VerificationStatus ToStatus { get; private set; }

    public string? Stage { get; private set; }

    public string? NoteCode { get; private set; }

    public VerificationActorType ActorType { get; private set; }

    public Guid? ActorId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static VerificationEvent Create(
        Guid clientId, Guid requestId, VerificationStatus? from, VerificationStatus to, string? stage, string? noteCode,
        VerificationActorType actorType, Guid? actorId, DateTime now) =>
        new()
        {
            ClientId = clientId,
            RequestId = requestId,
            FromStatus = from,
            ToStatus = to,
            Stage = stage is { Length: > 30 } s ? s[..30] : stage,
            NoteCode = noteCode is { Length: > 60 } n ? n[..60] : noteCode,
            ActorType = actorType,
            ActorId = actorId,
            CreatedAt = now,
        };
}

/// <summary>Single-use, short-lived, authenticated download grant. Only the SHA-256 of the token is stored.</summary>
public sealed class VerificationDownloadGrant : Entity, ITenantOwned
{
    private VerificationDownloadGrant()
    {
    }

    public Guid ClientId { get; set; }

    public Guid RequestId { get; private set; }

    public DownloadKind Kind { get; private set; }

    public string? Format { get; private set; }

    public Guid PrincipalId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public DateTime ExpiresAt { get; private set; }

    public DateTime? UsedAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public static VerificationDownloadGrant Create(
        Guid clientId, Guid requestId, DownloadKind kind, string? format, Guid principalId, byte[] tokenHash, DateTime now, TimeSpan lifetime) =>
        new()
        {
            ClientId = clientId,
            RequestId = requestId,
            Kind = kind,
            Format = format,
            PrincipalId = principalId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };

    public bool IsUsable(DateTime now) => UsedAt is null && ExpiresAt > now;
}

/// <summary>Platform switch for an Available module (global, no ClientId). Upcoming modules never have a row.</summary>
public sealed class VerificationModuleSetting
{
    private VerificationModuleSetting()
    {
    }

    public string ModuleKey { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static VerificationModuleSetting Create(string moduleKey, bool isEnabled, DateTime now, Guid? updatedBy = null) =>
        new() { ModuleKey = moduleKey, IsEnabled = isEnabled, UpdatedAt = now, UpdatedBy = updatedBy };

    public void SetEnabled(bool isEnabled, DateTime now, Guid? updatedBy)
    {
        IsEnabled = isEnabled;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
    }
}
