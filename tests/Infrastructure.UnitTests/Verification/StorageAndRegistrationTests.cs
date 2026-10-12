using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NexaVerify.Application.Abstractions;
using NexaVerify.Application.Verification;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Application.Verification.Processing;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Verification;
using NexaVerify.Infrastructure.Verification.Storage;

namespace NexaVerify.Infrastructure.UnitTests.Verification;

public class SqlDocumentStorageTests
{
    private sealed class XorEncryption : IClientEncryption
    {
        public Task<byte[]> EncryptAsync(Guid clientId, ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken) =>
            Task.FromResult(Encoding.UTF8.GetBytes(clientId.ToString("N") + purpose + "|").Concat(plaintext.ToArray().Select(b => (byte)(b ^ 0x33))).ToArray());

        public Task<byte[]> DecryptAsync(Guid clientId, ReadOnlyMemory<byte> payload, string purpose, CancellationToken cancellationToken)
        {
            var prefix = Encoding.UTF8.GetBytes(clientId.ToString("N") + purpose + "|");
            var data = payload.ToArray();
            return data.AsSpan().StartsWith(prefix)
                ? Task.FromResult(data.Skip(prefix.Length).Select(b => (byte)(b ^ 0x33)).ToArray())
                : throw new CryptographicException("purpose");
        }

        public Task DestroyKeysAsync(Guid clientId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Store : IVerificationDocumentStore
    {
        public Dictionary<Guid, DocumentCiphertext> Ciphertexts { get; } = [];

        public void Add(VerificationDocument document, VerificationDocumentBlob blob) => Ciphertexts[document.RequestId] = new DocumentCiphertext(document.Id, document.RequestId, blob.Content);

        public Task<VerificationDocument?> GetAsync(Guid requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DocumentCiphertext?> GetCiphertextAsync(Guid requestId, CancellationToken cancellationToken) => Task.FromResult(Ciphertexts.GetValueOrDefault(requestId));
    }

    private sealed class Tenant(Guid? clientId) : ITenantContext
    {
        public Guid? ClientId { get; } = clientId;

        public bool IsPlatform => false;
    }

    private static DocumentInspection Inspection(byte[] content) =>
        new(DocumentKind.Pdf, "application/pdf", ".pdf", 2, null, null, false, false, SHA256.HashData(content));

    [Fact]
    public async Task A_sealed_document_is_ciphertext_bound_to_its_request_and_reads_back_only_in_the_owning_tenant()
    {
        var client = Guid.NewGuid();
        var request = Guid.NewGuid();
        var content = Encoding.ASCII.GetBytes("%PDF-1.4 synthetic statement body");
        var store = new Store();
        var service = new SqlDocumentStorageService(new XorEncryption(), store, null!, new Tenant(client), TimeProvider.System);

        var sealedDoc = await service.SealAsync(client, request, new DocumentUpload("../../x.pdf", "application/pdf", content), Inspection(content), DateTime.UtcNow.AddDays(7), CancellationToken.None);

        sealedDoc.Document.RequestId.ShouldBe(request);
        sealedDoc.Document.SizeBytes.ShouldBe(content.Length);
        sealedDoc.Document.Extension.ShouldBe(".pdf"); // the declared file name is never kept
        sealedDoc.Blob.DocumentId.ShouldBe(sealedDoc.Document.Id);
        Encoding.ASCII.GetString(sealedDoc.Blob.Content).ShouldNotContain("synthetic statement");

        store.Add(sealedDoc.Document, sealedDoc.Blob);
        (await service.ReadAsync(request, CancellationToken.None)).Value.ShouldBe(content);

        // Another tenant, a ciphertext moved to another request, or a request without a document: never readable.
        var other = new SqlDocumentStorageService(new XorEncryption(), store, null!, new Tenant(Guid.NewGuid()), TimeProvider.System);
        (await other.ReadAsync(request, CancellationToken.None)).IsFailure.ShouldBeTrue();
        store.Ciphertexts[Guid.NewGuid()] = new DocumentCiphertext(Guid.NewGuid(), Guid.NewGuid(), sealedDoc.Blob.Content);
        var moved = store.Ciphertexts.Keys.Last();
        (await service.ReadAsync(moved, CancellationToken.None)).IsFailure.ShouldBeTrue();
        (await service.ReadAsync(Guid.NewGuid(), CancellationToken.None)).IsFailure.ShouldBeTrue();
        (await new SqlDocumentStorageService(new XorEncryption(), store, null!, new Tenant(null), TimeProvider.System).ReadAsync(request, CancellationToken.None)).IsFailure.ShouldBeTrue();
    }
}

public class VerificationHostingRegistrationTests
{
    [Fact]
    public void The_worker_the_retention_sweeper_and_the_storage_are_registered_and_options_are_validated_at_start()
    {
        var cfg = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerificationApplication();
        services.AddVerificationInfrastructure(cfg);

        services.Count(d => d.ServiceType == typeof(IHostedService)).ShouldBe(2);
        services.Any(d => d.ServiceType == typeof(IDocumentStorageService) && d.ImplementationType == typeof(SqlDocumentStorageService)).ShouldBeTrue();
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<VerificationWorker>().ShouldNotBeNull();
        sp.GetRequiredService<VerificationRetentionSweeper>().ShouldNotBeNull();
        sp.GetRequiredService<IOptions<VerificationRetentionJobOptions>>().Value.GrantRetentionHours.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void An_inconsistent_worker_lease_stops_the_host_at_start()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Verification:Processing:TimeoutSeconds"] = "120", ["Verification:Worker:LeaseSeconds"] = "100",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerificationApplication();
        services.AddVerificationInfrastructure(cfg);
        using var sp = services.BuildServiceProvider();
        Should.Throw<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<VerificationOptions>>().Value);
    }

    [Fact]
    public async Task Disabled_background_jobs_start_and_stop_without_touching_anything()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Verification:Worker:Enabled"] = "false", ["Verification:RetentionJob:Enabled"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddVerificationApplication();
        services.AddVerificationInfrastructure(cfg);
        using var sp = services.BuildServiceProvider();

        foreach (var job in sp.GetServices<IHostedService>())
        {
            await job.StartAsync(CancellationToken.None);
            await job.StopAsync(CancellationToken.None);
        }
    }
}
