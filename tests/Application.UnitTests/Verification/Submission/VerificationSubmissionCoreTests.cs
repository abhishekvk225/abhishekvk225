using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Common;
using NexaVerify.Application.UnitTests.Verification.Processing;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Modules;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Application.Verification.Parsing;
using NexaVerify.Application.Verification.Submission;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Verification;

namespace NexaVerify.Application.UnitTests.Verification.Submission;

public class VerificationSubmissionCoreTests
{
    private sealed class User : ICurrentUser
    {
        public bool IsAuthenticated { get; set; } = true;

        public ActorType ActorType { get; set; } = ActorType.User;

        public Guid? ActorId { get; set; } = Guid.NewGuid();

        public Guid? ClientId { get; set; } = Guid.NewGuid();

        public bool IsPlatformUser { get; set; }

        public IReadOnlyCollection<string> Roles { get; } = [];
    }

    private sealed class Info : IRequestInfo
    {
        public string? IpAddress => "203.0.113.9";

        public string? UserAgent => "tests";

        public string? CorrelationId => "corr-submit";
    }

    private sealed class Gate : IModuleGate
    {
        public bool Available { get; set; } = true;

        public Task<Result<VerificationModuleDescriptor>> EnsureAvailableAsync(VerificationType type, CancellationToken cancellationToken) =>
            Task.FromResult(Available
                ? Result<VerificationModuleDescriptor>.Success(ModuleCatalogue.Pan)
                : new Error(ErrorCodes.VerificationModuleDisabled, "off", ErrorType.NotFound));
    }

    private sealed class Inspector : IDocumentInspector
    {
        public Error? Fail { get; set; }

        public int Calls { get; private set; }

        public Task<Result<DocumentInspection>> InspectAsync(DocumentUpload upload, UploadPolicy policy, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Fail is null
                ? Result<DocumentInspection>.Success(new DocumentInspection(DocumentKind.Pdf, "application/pdf", ".pdf", 1, null, null, false, false, new byte[32]))
                : Fail);
        }
    }

    private sealed class Rig
    {
        public User User { get; } = new();

        public Gate Gate { get; } = new();

        public Inspector Inspector { get; } = new();

        public InMemoryRequests Requests { get; } = new();

        public ScriptedMeter Meter { get; } = new();

        public FakeCatalog Catalog { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public IOptions<VerificationOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new VerificationOptions());

        public VerificationSubmissionCore Core => new(
            User, new Info(), Gate, Inspector, new FakeStorage(), new VerificationEnvelopeCodec(new FakeEncryption()), Requests, Meter, Catalog, Audit,
            new PassThroughUnitOfWork(), Options, new FixedTime(ProcessorRig.Start));
    }

    private static SubmissionPlan Plan(SubmissionDocumentRule rule = SubmissionDocumentRule.Required, string? pan = null, string? holder = "ASHA TESTPERSON") => new(
        VerificationType.Pan, "ext-1", "consent-1",
        new Dictionary<string, string?> { [SubmittedFieldKeys.FullName] = holder, [SubmittedFieldKeys.PanNumber] = pan }, rule);

    private static DocumentUpload Upload(byte fill = 1, int size = 100) => new("card.pdf", "application/pdf", Enumerable.Repeat(fill, size).ToArray());

    [Fact]
    public async Task Only_client_principals_can_submit_and_the_tenant_is_the_credentials()
    {
        var rig = new Rig();
        rig.User.IsPlatformUser = true;
        rig.User.ClientId = null;
        var platform = await rig.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None);
        platform.Error!.Type.ShouldBe(ErrorType.Forbidden);

        var client = new Rig();
        var ok = await client.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None);
        ok.IsSuccess.ShouldBeTrue();
        client.Requests.Created!.ClientId.ShouldBe(client.User.ClientId!.Value);
        client.Requests.Created.Status.ShouldBe(VerificationStatus.Queued);
        client.Requests.Created.IpAddress.ShouldBe("203.0.113.9");
        client.Requests.Created.CorrelationId.ShouldBe("corr-submit");
    }

    [Fact]
    public async Task Nothing_is_inspected_stored_or_charged_when_an_early_check_fails()
    {
        var disabled = new Rig();
        disabled.Gate.Available = false;
        (await disabled.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None)).Error!.Code.ShouldBe(ErrorCodes.VerificationModuleDisabled);

        var noFile = new Rig();
        (await noFile.Core.SubmitAsync(Plan(), null, null, CancellationToken.None)).Error!.Code.ShouldBe(ErrorCodes.DocumentRequired);

        var tooLarge = new Rig();
        tooLarge.Options.Value.Upload.MaxFileBytes = 50;
        (await tooLarge.Core.SubmitAsync(Plan(), Upload(size: 51), null, CancellationToken.None)).Error!.Code.ShouldBe(ErrorCodes.DocumentTooLarge);

        var queueFull = new Rig();
        queueFull.Requests.OpenCount = queueFull.Options.Value.Limits.MaxQueuedPerClient;
        (await queueFull.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None)).Error!.Code.ShouldBe(ErrorCodes.VerificationQueueFull);

        var broke = new Rig();
        broke.Meter.PreflightError = Error.PaymentRequired(ErrorCodes.LicenseInsufficientBalance, "no");
        (await broke.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None)).Error!.Type.ShouldBe(ErrorType.PaymentRequired);
        broke.Inspector.Calls.ShouldBe(0); // credits are checked before the document is touched

        var longKey = new Rig();
        (await longKey.Core.SubmitAsync(Plan(), Upload(), new string('k', 101), CancellationToken.None)).Error!.Type.ShouldBe(ErrorType.Validation);

        var bad = new Rig();
        bad.Inspector.Fail = new Error(ErrorCodes.DocumentUnsupportedType, "no", ErrorType.Validation);
        (await bad.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None)).Error!.Code.ShouldBe(ErrorCodes.DocumentUnsupportedType);

        foreach (var rig in new[] { disabled, noFile, tooLarge, queueFull, broke, longKey, bad })
        {
            rig.Requests.Created.ShouldBeNull();
            rig.Meter.Charges.ShouldBeEmpty();
            rig.Audit.Entries.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_pan_may_omit_the_file_only_when_it_supplies_the_number()
    {
        var rig = new Rig();
        var empty = await rig.Core.SubmitAsync(Plan(SubmissionDocumentRule.DocumentOrNumber), null, null, CancellationToken.None);
        empty.Error!.Type.ShouldBe(ErrorType.Validation);
        empty.Error.FieldErrors!.ShouldContainKey("document");

        var numberOnly = await rig.Core.SubmitAsync(Plan(SubmissionDocumentRule.DocumentOrNumber, pan: "ABCPT1234F"), null, null, CancellationToken.None);
        numberOnly.IsSuccess.ShouldBeTrue();
        rig.Requests.Created!.UploadState.ShouldBe(StageState.Skipped);
        rig.Requests.CreatedDocument.ShouldBeNull();
        rig.Inspector.Calls.ShouldBe(0);

        var emptyFile = new DocumentUpload("empty.pdf", "application/pdf", []);
        (await rig.Core.SubmitAsync(Plan(), emptyFile, null, CancellationToken.None)).Error!.Code.ShouldBe(ErrorCodes.DocumentRequired); // an empty part is "no file"
    }

    [Fact]
    public async Task The_provider_cost_is_part_of_the_pre_flight_only_when_a_provider_is_configured()
    {
        var without = new Rig();
        await without.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None);
        without.Meter.Preflights.ShouldBe(["all:False"]);

        var with = new Rig();
        with.Catalog.Configured = true;
        await with.Core.SubmitAsync(Plan(), Upload(), null, CancellationToken.None);
        with.Meter.Preflights.ShouldBe(["all:True"]);
    }

    [Fact]
    public async Task A_replay_returns_the_first_request_without_inspecting_storing_or_auditing_again()
    {
        var rig = new Rig();
        var first = await rig.Core.SubmitAsync(Plan(), Upload(), "key-1", CancellationToken.None);
        var firstId = first.Value.RequestId;
        first.Value.Replayed.ShouldBeFalse();
        var inspected = rig.Inspector.Calls;
        var audits = rig.Audit.Entries.Count;
        rig.Requests.Created = null;

        var again = await rig.Core.SubmitAsync(Plan(), Upload(), "key-1", CancellationToken.None);

        again.Value.RequestId.ShouldBe(firstId);
        again.Value.Replayed.ShouldBeTrue();
        rig.Inspector.Calls.ShouldBe(inspected);
        rig.Audit.Entries.Count.ShouldBe(audits);
        rig.Requests.Created.ShouldBeNull();
    }

    [Fact]
    public async Task The_idempotency_fingerprint_binds_the_key_to_the_client_document_and_fields()
    {
        var client = Guid.NewGuid();
        var plan = Plan(pan: "ABCPT1234F");
        var doc = new byte[] { 1, 2, 3 };
        var baseline = VerificationSubmissionCore.DeriveKey(client, plan, "key", doc);

        VerificationSubmissionCore.DeriveKey(client, plan, "key", doc).ShouldBe(baseline);
        VerificationSubmissionCore.DeriveKey(Guid.NewGuid(), plan, "key", doc).ShouldNotBe(baseline); // another client
        VerificationSubmissionCore.DeriveKey(client, plan, "other", doc).ShouldNotBe(baseline);
        VerificationSubmissionCore.DeriveKey(client, plan, "key", [1, 2, 4]).ShouldNotBe(baseline); // another document
        VerificationSubmissionCore.DeriveKey(client, Plan(pan: "ABCPT1234G"), "key", doc).ShouldNotBe(baseline); // other fields
        VerificationSubmissionCore.DeriveKey(client, plan with { Type = VerificationType.Passport }, "key", doc).ShouldNotBe(baseline); // another type
        baseline.Length.ShouldBe(64);
        baseline.ShouldNotContain("key");
    }

    [Fact]
    public async Task The_accepted_request_stores_the_submitted_fields_only_sealed_and_audits_ids_only()
    {
        var rig = new Rig();
        var result = await rig.Core.SubmitAsync(Plan(pan: "ABCPT1234F", holder: ProcessorRig.CanaryName), Upload(), "k", CancellationToken.None);

        result.Value.StatusUrl.ShouldBe($"/api/v1/verifications/{result.Value.RequestId}/status");
        result.Value.Status.ShouldBe("Queued");
        var sealedRow = rig.Requests.CreatedSubmission!;
        System.Text.Encoding.UTF8.GetString(sealedRow.DataEnc).ShouldNotContain(ProcessorRig.CanaryName); // sealed: the plaintext is not readable in the stored row
        var opened = await new VerificationEnvelopeCodec(new FakeEncryption()).OpenAsync(rig.User.ClientId!.Value, result.Value.RequestId, sealedRow.DataEnc, CancellationToken.None);
        opened!.Submitted[SubmittedFieldKeys.FullName].ShouldBe(ProcessorRig.CanaryName);
        opened.Extracted.ShouldBeNull();

        var audit = rig.Audit.Entries.ShouldHaveSingleItem();
        audit.Action.ShouldBe("verification.submitted");
        System.Text.Json.JsonSerializer.Serialize(audit.NewValues).ShouldNotContain(ProcessorRig.CanaryName);
        rig.Requests.Created!.DocumentSha256.ShouldNotBeNull();
        rig.Requests.Created.ExpiresAt!.Value.ShouldBe(ProcessorRig.Start.UtcDateTime.AddDays(Math.Max(rig.Options.Value.Retention.DocumentDays, rig.Options.Value.Retention.ResultDays)));
    }
}
