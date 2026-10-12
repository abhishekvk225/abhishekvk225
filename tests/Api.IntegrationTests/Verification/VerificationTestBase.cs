using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Verification.Abstractions;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Verification;
using NexaVerify.Domain.Licensing;
using NexaVerify.Domain.Verification;
using NexaVerify.Infrastructure.Background;
using NexaVerify.Infrastructure.Persistence;
using NexaVerify.TestSupport;
using NexaVerify.TestSupport.Verification;

namespace NexaVerify.Api.IntegrationTests.Verification;

/// <summary>
/// Shared set-up for the M14 API tests: the real API on a fresh SQL Server database, the background worker and retention job switched OFF
/// (tests drive them by hand, so every state is deterministic), no OCR engine (digital PDFs only) and the deterministic provider fakes.
/// Nothing here is a production code path: the fakes exist only in this test host.
/// </summary>
public abstract class VerificationTestBase : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;

    protected VerificationTestBase(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected AuthApp App { get; private set; } = null!;

    protected LoginResponse Platform { get; private set; } = null!;

    protected FakePanVerificationProvider Pan { get; } = new() { Mode = FakeProviderMode.ByNumberSuffix };

    protected FakePassportVerificationProvider Passport { get; } = new();

    /// <summary>Use the fake PAN / passport providers (selected by id). When false the product default applies: no provider configured.</summary>
    protected virtual bool UseFakeProviders => true;

    protected virtual IReadOnlyDictionary<string, string> ExtraSettings => new Dictionary<string, string>();

    public async Task InitializeAsync()
    {
        var settings = new Dictionary<string, string>
        {
            ["Verification:Worker:Enabled"] = "false",
            ["Verification:RetentionJob:Enabled"] = "false",
            ["Verification:Ocr:Provider"] = "none",
            ["Verification:Worker:BatchSize"] = "50",
            ["Verification:Worker:PerClientLimit"] = "50",
            ["Verification:Worker:Parallelism"] = "6",
            ["Verification:Providers:Pan:TimeoutSeconds"] = "1",
            ["Verification:Providers:Passport:TimeoutSeconds"] = "1",
            ["Throttle:VerificationSubmitsPerMinute"] = "10000",
            ["Throttle:VerificationDownloadsPerMinute"] = "10000",
        };
        if (UseFakeProviders)
        {
            settings["Verification:Providers:Pan:Provider"] = Pan.ProviderId;
            settings["Verification:Providers:Passport:Provider"] = Passport.ProviderId;
        }

        foreach (var (key, value) in ExtraSettings)
        {
            settings[key] = value;
        }

        App = await AuthApp.CreateAsync(_fixture, settings, services =>
        {
            if (UseFakeProviders)
            {
                services.AddSingleton<IPanVerificationProvider>(Pan);
                services.AddSingleton<IPassportVerificationProvider>(Passport);
            }

            ConfigureServices(services);
        });
        Platform = await App.SuperAdminAsync();
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public virtual async Task DisposeAsync() => await App.DisposeAsync();

    protected sealed record Tenant(Guid ClientId, string Token, Guid LicenseId);

    protected async Task<Tenant> NewTenantAsync(string code, int credits = 1000)
    {
        var (client, admin) = await App.OnboardClientAsync(Platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        var license = await App.SeedLicenseAsync(client.Id, credits);
        return new Tenant(client.Id, admin.AccessToken, license);
    }

    // ---- requests -------------------------------------------------------------------------------------------------------------

    protected const string Consent = "consent-2026-001";

    protected Task<HttpResponseMessage> PostDocumentAsync(
        string path, string? token, byte[]? document, IReadOnlyDictionary<string, string>? fields, string? idempotencyKey = null,
        string fileName = "document.pdf", string contentType = "application/pdf")
    {
        var form = new MultipartFormDataContent();
        foreach (var (key, value) in fields ?? new Dictionary<string, string>())
        {
            form.Add(new StringContent(value), key);
        }

        if (document is not null)
        {
            var part = new ByteArrayContent(document);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(part, "document", fileName);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return App.SendAsync(request, token);
    }

    protected Task<HttpResponseMessage> SubmitBankAsync(Tenant t, byte[]? pdf = null, string? key = null, IReadOnlyDictionary<string, string>? fields = null) =>
        PostDocumentAsync("/api/v1/verifications/bank-statements", t.Token, pdf ?? BankPdf(), fields ?? BankFields(), key);

    protected Task<HttpResponseMessage> SubmitPanAsync(Tenant t, byte[]? pdf, IReadOnlyDictionary<string, string>? fields = null, string? key = null) =>
        PostDocumentAsync("/api/v1/verifications/pan", t.Token, pdf, fields ?? PanFields(), key);

    protected Task<HttpResponseMessage> SubmitPassportAsync(Tenant t, byte[]? pdf = null, IReadOnlyDictionary<string, string>? fields = null, string? key = null) =>
        PostDocumentAsync("/api/v1/verifications/passports", t.Token, pdf ?? PassportPdf(), fields ?? PassportFields(), key);

    protected static byte[] BankPdf(string? extraLine = null) =>
        SampleDocuments.Pdf(extraLine is null ? SampleTexts.GenericStatement() : [.. SampleTexts.GenericStatement(), extraLine]);

    protected static byte[] PanPdf(string pan = SampleTexts.PanNumber) => SampleDocuments.Pdf(SampleTexts.PanCard(pan));

    protected static byte[] PassportPdf() => SampleDocuments.Pdf(SampleTexts.PassportPage());

    protected static Dictionary<string, string> BankFields() => new()
    {
        ["consentReference"] = Consent,
        ["accountHolderName"] = SampleTexts.Holder,
        ["bankName"] = "Synthetic Test Bank",
        ["accountNumberLast4"] = "2233",
    };

    protected static Dictionary<string, string> PanFields(string pan = SampleTexts.PanNumber) => new()
    {
        ["consentReference"] = Consent,
        ["panNumber"] = pan,
        ["fullName"] = SampleTexts.Holder,
        ["dateOfBirth"] = "1990-08-15",
    };

    protected static Dictionary<string, string> PassportFields() => new()
    {
        ["consentReference"] = Consent,
        ["passportNumber"] = "X1234567",
        ["fullName"] = "ASHA TESTPERSON",
        ["dateOfBirth"] = "1990-08-15",
        ["nationality"] = "UTO",
    };

    // ---- reading --------------------------------------------------------------------------------------------------------------

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(text, AuthApp.Json) ?? throw new InvalidOperationException("Empty body: " + text);
    }

    protected static async Task<JsonElement> JsonOf(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json);

    protected static async Task<string?> CodeOf(HttpResponseMessage response) => (await JsonOf(response)).GetProperty("code").GetString();

    protected async Task<VerificationAcceptedDto> AcceptedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBeOneOf([System.Net.HttpStatusCode.Accepted, System.Net.HttpStatusCode.OK], await response.Content.ReadAsStringAsync());
        return await ReadAsync<VerificationAcceptedDto>(response);
    }

    protected async Task<VerificationResultDto> ResultAsync(Tenant t, Guid id)
    {
        var response = await App.GetAsync($"/api/v1/verifications/{id}", t.Token);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadAsync<VerificationResultDto>(response);
    }

    protected async Task<VerificationStatusDto> StatusAsync(Tenant t, Guid id)
    {
        var response = await App.GetAsync($"/api/v1/verifications/{id}/status", t.Token);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadAsync<VerificationStatusDto>(response);
    }

    // ---- driving the background pieces by hand --------------------------------------------------------------------------------

    protected Task<int> RunWorkerAsync() => App.Factory.Services.GetRequiredService<VerificationWorker>().RunOnceAsync(CancellationToken.None);

    protected Task RecoverAsync() => App.Factory.Services.GetRequiredService<VerificationWorker>().RecoverAsync(CancellationToken.None);

    protected Task<VerificationRetentionSweeper.SweepResult> SweepRetentionAsync() =>
        App.Factory.Services.GetRequiredService<VerificationRetentionSweeper>().SweepOnceAsync(CancellationToken.None);

    /// <summary>Submit, process and return the finished result in one go.</summary>
    protected async Task<VerificationResultDto> ProcessAsync(Tenant t, HttpResponseMessage submitted)
    {
        var accepted = await AcceptedAsync(submitted);
        (await RunWorkerAsync()).ShouldBeGreaterThan(0);
        return await ResultAsync(t, accepted.RequestId);
    }

    // ---- database views -------------------------------------------------------------------------------------------------------

    protected Task<T> InTenantAsync<T>(Tenant t, Func<AppDbContext, Task<T>> action) => App.WithTenantDbAsync(t.ClientId, action);

    protected Task<int> ConsumedAsync(Tenant t) =>
        App.WithDbAsync(async db => (await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == t.LicenseId)).ConsumedCredits);

    protected Task<List<LicenseTransaction>> LedgerRowsAsync(Tenant t) =>
        InTenantAsync(t, db => db.LicenseTransactions.AsNoTracking().Where(x => x.Type == LedgerEntryType.Consume).OrderBy(x => x.Id).ToListAsync());

    protected Task<VerificationRequest> RequestRowAsync(Tenant t, Guid id) =>
        InTenantAsync(t, db => db.VerificationRequests.AsNoTracking().SingleAsync(r => r.Id == id));
}
