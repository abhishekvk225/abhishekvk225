using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Licensing;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests.Support;

/// <summary>Shared set-up for the M7 tests: a real API on a fresh database, a Super Admin, and tenants with licenses and recognitions.</summary>
public abstract class UsageTestBase : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;

    protected UsageTestBase(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected AuthApp App { get; private set; } = null!;

    protected LoginResponse Platform { get; private set; } = null!;

    protected virtual IReadOnlyDictionary<string, string>? Settings => null;

    public async Task InitializeAsync()
    {
        App = await AuthApp.CreateAsync(_fixture, Settings);
        Platform = await App.SuperAdminAsync();
    }

    public virtual async Task DisposeAsync() => await App.DisposeAsync();

    protected sealed record Tenant(Guid ClientId, string Token, Guid LicenseId);

    protected async Task<Tenant> NewTenantAsync(string code, int credits = 200, DateTime? licenseEnds = null)
    {
        var (client, admin) = await App.OnboardClientAsync(Platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        var license = await App.SeedLicenseAsync(client.Id, credits, expiresAt: licenseEnds);
        return new Tenant(client.Id, admin.AccessToken, license);
    }

    protected Task<HttpResponseMessage> EnrollAsync(string token, string externalRef, byte[] image) =>
        App.PostFormAsync("/api/v1/faces/enroll", image, new Dictionary<string, string> { ["externalRef"] = externalRef, ["consentReference"] = "consent-1" }, token);

    protected Task<HttpResponseMessage> VerifyAsync(string token, string externalRef, byte[] image) =>
        App.PostFormAsync("/api/v1/faces/verify", image, new Dictionary<string, string> { ["externalRef"] = externalRef }, token);

    protected Task<HttpResponseMessage> IdentifyAsync(string token, byte[] image) =>
        App.PostFormAsync("/api/v1/faces/identify", image, null, token);

    /// <summary>
    /// Two enrolments (1 credit each), a matching and a non-matching verification (1 each) and one identification (2): 6 credits,
    /// 5 recognitions of which 4 succeed and 1 is "no match". Uses distinct images, because identical requests replay instead of charging.
    /// </summary>
    protected async Task<int> RunRecognitionsAsync(Tenant t, int seed)
    {
        (await EnrollAsync(t.Token, "emp-1", TestImages.Person(seed))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EnrollAsync(t.Token, "emp-2", TestImages.Person(seed + 1))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await VerifyAsync(t.Token, "emp-1", TestImages.Person(seed, variation: 3))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await VerifyAsync(t.Token, "emp-1", TestImages.Person(seed + 1, variation: 4))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IdentifyAsync(t.Token, TestImages.Person(seed + 1, variation: 2))).StatusCode.ShouldBe(HttpStatusCode.OK);
        return 6;
    }

    protected async Task<T> GetAsync<T>(string path, string token)
    {
        var response = await App.GetAsync(path, token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(AuthApp.Json))!;
    }

    /// <summary>Ledger totals of one client straight from the table, as the tenant would see them.</summary>
    protected Task<(long Consumed, long Refunded, long ChargedOnRecognitions)> LedgerTotalsAsync(Guid clientId) =>
        App.WithTenantDbAsync(clientId, async db =>
        {
            var consumed = -await db.LicenseTransactions.Where(x => x.Type == LedgerEntryType.Consume).SumAsync(x => (long?)x.Credits) ?? 0;
            var refunded = await db.LicenseTransactions.Where(x => x.Type == LedgerEntryType.Refund).SumAsync(x => (long?)x.Credits) ?? 0;
            var charged = await db.RecognitionRequests.SumAsync(r => (long?)r.CreditsCharged) ?? 0;
            return (consumed, refunded, charged);
        });
}
