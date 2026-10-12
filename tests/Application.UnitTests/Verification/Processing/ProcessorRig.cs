using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Api;
using NexaVerify.Application.Auditing;
using NexaVerify.Application.Common;
using NexaVerify.Application.Licensing;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Billing;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Providers;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Contracts.Common;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Application.UnitTests.Verification.Processing;

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Reversible, purpose-bound stand-in for the client key service (not cryptography: the real one is tested elsewhere).</summary>
internal sealed class FakeEncryption : IClientEncryption
{
    public Task<byte[]> EncryptAsync(Guid clientId, ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken) =>
        Task.FromResult(Encoding.UTF8.GetBytes(clientId.ToString("N") + "|" + purpose + "|").Concat(plaintext.ToArray().Select(b => (byte)(b ^ 0x5A))).ToArray());

    public Task<byte[]> DecryptAsync(Guid clientId, ReadOnlyMemory<byte> payload, string purpose, CancellationToken cancellationToken)
    {
        var prefix = Encoding.UTF8.GetBytes(clientId.ToString("N") + "|" + purpose + "|");
        var data = payload.ToArray();
        if (data.Length < prefix.Length || !data.AsSpan(0, prefix.Length).SequenceEqual(prefix))
        {
            throw new CryptographicException("wrong client or purpose");
        }

        return Task.FromResult(data.Skip(prefix.Length).Select(b => (byte)(b ^ 0x5A)).ToArray());
    }

    public Task DestroyKeysAsync(Guid clientId, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FakeCrypto : IPlatformCrypto
{
    public string KeyId => "k1";

    public bool HasKey(string keyId) => true;

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose, string context) => throw new NotSupportedException();

    public byte[] Unprotect(ReadOnlySpan<byte> payload, string purpose, string context) => throw new NotSupportedException();

    public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data) => HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-key:" + purpose), data);

    public byte[] ComputeMac(string purpose, ReadOnlySpan<byte> data, string keyId) => ComputeMac(purpose, data);
}

internal sealed class RecordingAudit : IAuditService
{
    public List<AuditEntry> Entries { get; } = [];

    public void Record(AuditEntry entry) => Entries.Add(entry);
}

internal sealed class RecordingWebhooks : IWebhookPublisher
{
    public List<(Guid ClientId, string Event, object Data)> Published { get; } = [];

    public Task PublishAsync(Guid clientId, string eventType, object data, CancellationToken cancellationToken)
    {
        Published.Add((clientId, eventType, data));
        return Task.CompletedTask;
    }
}

internal sealed class PassThroughUnitOfWork : IUnitOfWork
{
    /// <summary>The next N saves throw an optimistic-concurrency conflict (somebody else changed the row).</summary>
    public int ConflictsToThrow { get; set; }

    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ConflictsToThrow > 0)
        {
            ConflictsToThrow--;
            throw new ConcurrencyConflictException(new InvalidOperationException("row version"));
        }

        Saves++;
        return Task.FromResult(1);
    }

    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) => action(cancellationToken);

    public void ClearTracked()
    {
    }
}

internal sealed class InMemoryRequests : IVerificationRepository
{
    public Dictionary<Guid, VerificationRequest> Rows { get; } = [];

    public VerificationRequest? Created { get; set; }

    public VerificationResult? CreatedSubmission { get; private set; }

    public VerificationDocument? CreatedDocument { get; private set; }

    public int OpenCount { get; set; }

    public void Add(VerificationRequest request) => Rows[request.Id] = request;

    public Task<VerificationRequest?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Rows.GetValueOrDefault(id));

    public Task<VerificationRequest?> GetReadOnlyAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Rows.GetValueOrDefault(id));

    public Task<VerificationRequest?> FindByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Values.FirstOrDefault(r => r.IdempotencyKey == idempotencyKey));

    public Task<VerificationCreateOutcome> CreateIdempotentAsync(
        VerificationRequest request, VerificationDocument? document, VerificationDocumentBlob? blob, CancellationToken cancellationToken, VerificationResult? submission = null)
    {
        Created = request;
        CreatedDocument = document;
        CreatedSubmission = submission;
        Rows[request.Id] = request;
        return Task.FromResult(new VerificationCreateOutcome(request, true));
    }

    public Task<bool> TrySaveAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<int> CountOpenAsync(CancellationToken cancellationToken) => Task.FromResult(OpenCount);

    public Task<Guid?> FindLatestByDocumentHashAsync(byte[] sha256, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

    public Task<(IReadOnlyList<VerificationListRow> Items, int Total)> ListAsync(VerificationListFilter filter, int skip, int take, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<VerificationEvent>> ListEventsAsync(Guid requestId, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class InMemoryResults : IVerificationResultStore
{
    public Dictionary<Guid, VerificationResult> Rows { get; } = [];

    public List<VerificationCheck> Checks { get; } = [];

    public int ReplaceResultCalls { get; private set; }

    public Task<VerificationResult?> GetResultAsync(Guid requestId, CancellationToken cancellationToken) => Task.FromResult(Rows.GetValueOrDefault(requestId));

    public Task ReplaceResultAsync(VerificationResult result, CancellationToken cancellationToken)
    {
        ReplaceResultCalls++;
        Rows[result.RequestId] = result;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VerificationCheck>> ListChecksAsync(Guid requestId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VerificationCheck>>(Checks.Where(c => c.RequestId == requestId).OrderBy(c => c.Ordinal).ToList());

    public Task ReplaceChecksAsync(Guid requestId, IReadOnlyCollection<CheckStage> stages, IEnumerable<VerificationCheck> checks, CancellationToken cancellationToken)
    {
        Checks.RemoveAll(c => c.RequestId == requestId && stages.Contains(c.Stage));
        Checks.AddRange(checks);
        return Task.CompletedTask;
    }

    public Task<int> CountByIdentifierHashAsync(byte[] identifierHash, Guid? excludingRequestId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class InMemoryDocuments : IVerificationDocumentStore
{
    public Dictionary<Guid, VerificationDocument> Rows { get; } = [];

    public void Add(VerificationDocument document, VerificationDocumentBlob blob) => Rows[document.RequestId] = document;

    public Task<VerificationDocument?> GetAsync(Guid requestId, CancellationToken cancellationToken) => Task.FromResult(Rows.GetValueOrDefault(requestId));

    public Task<DocumentCiphertext?> GetCiphertextAsync(Guid requestId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class RecordingRetention(InMemoryResults results) : IVerificationRetention
{
    public List<Guid> Erased { get; } = [];

    public Task<bool> EraseAsync(Guid requestId, DateTime now, CancellationToken cancellationToken)
    {
        Erased.Add(requestId);
        results.Rows.Remove(requestId);
        results.Checks.RemoveAll(c => c.RequestId == requestId);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<DueVerification>> ListRequestsDueAsync(DateTime now, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> ListDocumentsDueAsync(DateTime now, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> ListResultsDueAsync(DateTime now, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> PurgeDocumentAsync(Guid requestId, DateTime now, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> PurgeResultAsync(Guid requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> PurgeExpiredGrantsAsync(DateTime expiredBefore, int take, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> BlankIpAddressesAsync(DateTime createdBefore, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeStorage : IDocumentStorageService
{
    public byte[] Content { get; set; } = [1, 2, 3];

    public Task<SealedDocument> SealAsync(Guid clientId, Guid requestId, DocumentUpload upload, DocumentInspection inspection, DateTime purgeAfter, CancellationToken cancellationToken)
    {
        var document = VerificationDocument.Create(clientId, requestId, inspection.Kind, inspection.ContentType, inspection.Extension, upload.Content.Length, inspection.PageCount, inspection.Sha256, purgeAfter);
        return Task.FromResult(new SealedDocument(document, VerificationDocumentBlob.Create(clientId, document.Id, [9])));
    }

    public Task<Result<byte[]>> ReadAsync(Guid requestId, CancellationToken cancellationToken) => Task.FromResult(Result<byte[]>.Success(Content));

    public Task DeleteAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class ScriptedMeter : IVerificationMeter
{
    public Error? PreflightError { get; set; }

    public Error? StagePreflightError { get; set; }

    public Dictionary<VerificationStage, Error> ChargeErrors { get; } = [];

    public int Remaining { get; set; } = 500;

    public List<(VerificationStage Stage, MeterOutcome Outcome, Guid RequestId)> Charges { get; } = [];

    public List<string> Preflights { get; } = [];

    public Task<Result<VerificationMeterTicket>> PreflightAsync(Guid clientId, VerificationType type, bool includeProvider, CancellationToken cancellationToken)
    {
        Preflights.Add("all:" + includeProvider);
        return Task.FromResult(PreflightError is null ? Result<VerificationMeterTicket>.Success(new VerificationMeterTicket(1, 0, 100)) : PreflightError);
    }

    public Task<Result<VerificationMeterTicket>> PreflightStageAsync(Guid clientId, VerificationType type, VerificationStage stage, CancellationToken cancellationToken)
    {
        Preflights.Add("stage:" + stage);
        return Task.FromResult(StagePreflightError is null ? Result<VerificationMeterTicket>.Success(new VerificationMeterTicket(1, 0, 100)) : StagePreflightError);
    }

    public Task<Result<StageCharge>> ChargeStageAsync(Guid clientId, Guid requestId, VerificationType type, VerificationStage stage, MeterOutcome outcome, CancellationToken cancellationToken)
    {
        if (ChargeErrors.TryGetValue(stage, out var error))
        {
            return Task.FromResult<Result<StageCharge>>(error);
        }

        Charges.Add((stage, outcome, requestId));
        var billable = outcome is MeterOutcome.Success or MeterOutcome.NoMatch;
        var credits = billable ? (stage == VerificationStage.Extraction ? 5 : 2) : 0;
        Remaining -= credits;
        return Task.FromResult(Result<StageCharge>.Success(new StageCharge(credits, Remaining, billable ? Charges.Count : null, false)));
    }
}

internal sealed class FakeCatalog : IVerificationProviderCatalog
{
    public bool Configured { get; set; }

    public bool HasProviderStage(VerificationType type) => type != VerificationType.BankStatement;

    public bool IsConfigured(VerificationType type) => Configured && HasProviderStage(type);

    public IReadOnlyList<ProviderStatus> Describe() => [];
}

internal sealed class ScriptedModule(VerificationType type) : IModuleProcessor
{
    public VerificationType Type { get; } = type;

    public Func<ModuleContext, CancellationToken, Task<DocumentStageOutcome>> Document { get; set; } = (_, _) => Task.FromResult(DefaultDocument(null));

    public Func<ModuleContext, CancellationToken, Task<ExternalStageOutcome>> External { get; set; } =
        (_, _) => Task.FromResult(new ExternalStageOutcome(ExternalState.Verified, "fake-pan", "ref-1", [new FieldMatch("name", FieldMatchOutcome.Match)], null));

    public int DocumentCalls { get; private set; }

    public int ExternalCalls { get; private set; }

    public ModuleContext? LastExternalContext { get; private set; }

    public static DocumentStageOutcome DefaultDocument(ExtractedData? data, IReadOnlyList<CheckResult>? checks = null, IReadOnlyList<ReviewReason>? reasons = null) =>
        new(StageState.Completed, StageState.Completed, StageState.Completed, data ?? new PanData { PanNumber = "ABCPT1234F", NameOnCard = "ASHA TESTPERSON" },
            checks ?? [new CheckResult("PAN_FORMAT", CheckStage.Rules, CheckOutcome.Pass, CheckSeverity.Info, "panNumber", "The number has the expected format."),
                       new CheckResult("PAN_NAME_MATCH", CheckStage.Consistency, CheckOutcome.Pass, CheckSeverity.Info, "fullName", "Match: submitted and extracted value of this field compared.")],
            reasons ?? [], null);

    public Task<DocumentStageOutcome> ProcessDocumentAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        DocumentCalls++;
        return Document(context, cancellationToken);
    }

    public Task<ExternalStageOutcome> VerifyExternalAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        ExternalCalls++;
        LastExternalContext = context;
        return External(context, cancellationToken);
    }
}

/// <summary>Everything the processor needs, wired to in-memory fakes. The request is created through the real submission shape so the domain state machine is exercised.</summary>
internal sealed class ProcessorRig
{
    public static readonly DateTimeOffset Start = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    public const string CanaryName = "CANARY PERSONNAME";

    public ProcessorRig(VerificationType type = VerificationType.Pan, bool withDocument = true)
    {
        Type = type;
        Module = new ScriptedModule(type);
        Retention = new RecordingRetention(Results);
        Options.Value.Processing.BackoffSeconds = [30, 120, 600];
        Processor = new VerificationProcessor(
            Requests, Documents, Results, Retention, Storage, Codec, [Module], Meter, Catalog, new FakeCrypto(), Audit, Webhooks, Uow,
            Options, Time, Logger);
        Seed(withDocument).GetAwaiter().GetResult();
    }

    public VerificationType Type { get; }

    public Guid ClientId { get; } = Guid.NewGuid();

    public Guid LeaseToken { get; } = Guid.NewGuid();

    public IOptions<VerificationOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new VerificationOptions());

    public FixedTime Time { get; } = new(Start);

    public InMemoryRequests Requests { get; } = new();

    public InMemoryDocuments Documents { get; } = new();

    public InMemoryResults Results { get; } = new();

    public RecordingRetention Retention { get; }

    public FakeStorage Storage { get; } = new();

    public VerificationEnvelopeCodec Codec { get; } = new(new FakeEncryption());

    public ScriptedMeter Meter { get; } = new();

    public FakeCatalog Catalog { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public RecordingWebhooks Webhooks { get; } = new();

    public PassThroughUnitOfWork Uow { get; } = new();

    public LogCapture Logs { get; } = new();

    public ILogger<VerificationProcessor> Logger => new CapturingLogger(Logs);

    public ScriptedModule Module { get; }

    public VerificationProcessor Processor { get; }

    public VerificationRequest Request { get; private set; } = null!;

    public ClaimedRequest Claim => new(Request.Id, ClientId, Type, LeaseToken, Request.Attempts);

    private async Task Seed(bool withDocument)
    {
        var now = Start.UtcDateTime;
        var id = Guid.CreateVersion7();
        Request = VerificationRequest.Create(
            id, ClientId, Type, SubmitSource.Api, VerificationActorType.ApiKey, Guid.NewGuid(), "ref-1", "consent-1", null,
            withDocument ? new byte[32] : null, withDocument ? 1 : null, "corr-1", null, now, now.AddDays(90));
        Requests.Add(Request);
        if (withDocument)
        {
            Documents.Rows[id] = VerificationDocument.Create(ClientId, id, DocumentKind.Pdf, "application/pdf", ".pdf", 100, 1, new byte[32], now.AddDays(7));
        }

        var envelope = new SubmissionEnvelope(
            new Dictionary<string, string?> { ["fullName"] = CanaryName, ["panNumber"] = "ABCPT1234F" }, null);
        Results.Rows[id] = VerificationResult.Create(ClientId, id, 1, await Codec.SealAsync(ClientId, id, envelope, CancellationToken.None), null, null, "[]", now.AddDays(90));
        Claim_();
    }

    /// <summary>The worker's claim: Queued to Processing under the rig's lease.</summary>
    private void Claim_() => Request.Claim(LeaseToken, Start.UtcDateTime.AddMinutes(5), Start.UtcDateTime);

    public Task RunAsync(CancellationToken cancellationToken = default) => Processor.ProcessAsync(Claim, cancellationToken);

    private sealed class CapturingLogger(LogCapture capture) : ILogger<VerificationProcessor>
    {
        private readonly ILogger _inner = capture.CreateLogger("p");

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
