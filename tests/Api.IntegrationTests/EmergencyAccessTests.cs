using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Api;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Break-glass API access controls: revoke all keys / one key, the client-wide kill switch, cache propagation and the corrupt allow-list rule.</summary>
[Collection(SqlServerCollection.Name)]
public class EmergencyAccessTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public EmergencyAccessTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["ApiAuth:CacheSeconds"] = "1", ["ApiAuth:NegativeCacheSeconds"] = "1" });
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private sealed record Tenant(Guid ClientId, string Token);

    private async Task<Tenant> NewTenantAsync(string code, int credits = 100)
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, code, $"a@{code.ToLowerInvariant()}.test");
        await _app.SeedLicenseAsync(client.Id, credits);
        return new Tenant(client.Id, admin.AccessToken);
    }

    private async Task<CreatedApiKeyDto> CreateKeyAsync(Tenant t, string name = "ci")
    {
        var response = await _app.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest(name, ["faces.read"], null, null, null), t.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!;
    }

    private static Task<HttpResponseMessage> BalanceAsync(HttpClient client, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/faces/balance");
        request.Headers.Add("X-Api-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("code").GetString();

    private static string Emergency(Guid client, string tail) => $"/api/v1/admin/clients/{client}/{tail}";

    [Fact]
    public async Task Revoking_all_keys_stops_every_key_of_that_client_at_once_and_leaves_other_clients_alone()
    {
        var a = await NewTenantAsync("E1");
        var b = await NewTenantAsync("E1B");
        var keys = new[] { await CreateKeyAsync(a, "one"), await CreateKeyAsync(a, "two"), await CreateKeyAsync(a, "three") };
        var other = await CreateKeyAsync(b);
        foreach (var key in keys.Append(other))
        {
            (await BalanceAsync(_app.Client, key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var response = await _app.PostAsync(Emergency(a.ClientId, "api-keys/revoke-all"), new EmergencyRevokeRequest("Keys leaked in a public repository"), _platform.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<EmergencyRevokeResultDto>(AuthApp.Json))!.RevokedKeys.ShouldBe(3);
        foreach (var key in keys)
        {
            var denied = await BalanceAsync(_app.Client, key.RawKey);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await CodeOf(denied)).ShouldBe(ErrorCodes.ApiKeyRevoked);
        }

        (await BalanceAsync(_app.Client, other.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await _app.WithTenantDbAsync(a.ClientId, db => db.ApiKeys.AsNoTracking().ToListAsync());
        stored.ShouldAllBe(k => k.Status == ApiKeyStatus.Revoked && k.RevokedReason!.Contains("Keys leaked") && k.RevokedBy != null);
        var audit = await _app.WithDbAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(l => l.Action == "apikey.emergency_revoked_all"));
        (audit.ClientId, audit.ActorId).ShouldBe((a.ClientId, _platform.User.Id));
        audit.NewValuesJson!.ShouldContain("Keys leaked in a public repository");
        audit.NewValuesJson!.ShouldNotContain(keys[0].RawKey);

        // repeating is harmless: nothing left to revoke
        (await (await _app.PostAsync(Emergency(a.ClientId, "api-keys/revoke-all"), new EmergencyRevokeRequest("again"), _platform.AccessToken)).Content.ReadFromJsonAsync<EmergencyRevokeResultDto>(AuthApp.Json))!
            .RevokedKeys.ShouldBe(0);
    }

    [Fact]
    public async Task Revoking_one_key_leaves_the_others_and_cannot_cross_clients()
    {
        var a = await NewTenantAsync("E2");
        var b = await NewTenantAsync("E2B");
        var first = await CreateKeyAsync(a, "first");
        var second = await CreateKeyAsync(a, "second");
        var foreign = await CreateKeyAsync(b);

        (await _app.PostAsync(Emergency(a.ClientId, $"api-keys/{foreign.Key.Id}/revoke"), new EmergencyRevokeRequest("x"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PostAsync(Emergency(a.ClientId, $"api-keys/{Guid.NewGuid()}/revoke"), new EmergencyRevokeRequest("x"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PostAsync(Emergency(a.ClientId, $"api-keys/{first.Key.Id}/revoke"), new EmergencyRevokeRequest(""), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var revoked = await _app.PostAsync(Emergency(a.ClientId, $"api-keys/{first.Key.Id}/revoke"), new EmergencyRevokeRequest("Compromised laptop"), _platform.AccessToken);
        revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await revoked.Content.ReadAsStringAsync());
        (await revoked.Content.ReadFromJsonAsync<ApiKeyDto>(AuthApp.Json))!.Status.ShouldBe("Revoked");

        (await BalanceAsync(_app.Client, first.RawKey)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await BalanceAsync(_app.Client, second.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BalanceAsync(_app.Client, foreign.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.PostAsync(Emergency(a.ClientId, $"api-keys/{first.Key.Id}/revoke"), new EmergencyRevokeRequest("twice"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.WithDbAsync(db => db.AuditLogs.CountAsync(l => l.Action == "apikey.emergency_revoked" && l.ClientId == a.ClientId))).ShouldBe(1);
    }

    [Fact]
    public async Task The_kill_switch_refuses_every_key_without_touching_them_and_can_be_switched_off_again()
    {
        var a = await NewTenantAsync("E3");
        var b = await NewTenantAsync("E3B");
        var key = await CreateKeyAsync(a);
        var other = await CreateKeyAsync(b);
        (await BalanceAsync(_app.Client, key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _app.PutAsync(Emergency(a.ClientId, "api-access"), new SetApiAccessRequest(true, ""), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var off = await _app.PutAsync(Emergency(a.ClientId, "api-access"), new SetApiAccessRequest(true, "Suspected abuse, investigating"), _platform.AccessToken);
        off.StatusCode.ShouldBe(HttpStatusCode.OK);

        var refused = await BalanceAsync(_app.Client, key.RawKey);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(refused)).ShouldBe(ErrorCodes.ApiAccessDisabled);
        (await BalanceAsync(_app.Client, other.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await _app.GetAsync(Emergency(a.ClientId, "api-access"), _platform.AccessToken)).Content.ReadFromJsonAsync<ApiAccessDto>(AuthApp.Json))!.Disabled.ShouldBeTrue();
        (await _app.WithTenantDbAsync(a.ClientId, db => db.ApiKeys.AsNoTracking().SingleAsync())).Status.ShouldBe(ApiKeyStatus.Active); // keys untouched
        (await _app.GetAsync("/api/v1/client/profile", a.Token)).StatusCode.ShouldBe(HttpStatusCode.OK); // the portal still works
        // the switch is a platform-managed setting: the client cannot flip it back through its own settings
        var tamper = await _app.PutAsync("/api/v1/client/settings/security", new { values = new Dictionary<string, bool> { ["api.accessDisabled"] = false } }, a.Token);
        tamper.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
        (await BalanceAsync(_app.Client, key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await _app.PutAsync(Emergency(a.ClientId, "api-access"), new SetApiAccessRequest(false, "False alarm"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await BalanceAsync(_app.Client, key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var audits = await _app.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(l => l.Action.StartsWith("client.api_access_")).Select(l => l.Action).ToListAsync());
        audits.ShouldBe(["client.api_access_disabled", "client.api_access_enabled"], ignoreOrder: true);
    }

    [Fact]
    public async Task Another_node_honours_a_revocation_and_the_kill_switch_within_the_cache_ttl()
    {
        var a = await NewTenantAsync("E4");
        var first = await CreateKeyAsync(a, "one");
        var second = await CreateKeyAsync(a, "two");
        await using var nodeB = new ApiFactory { ConnectionString = _app.ConnectionString, UseTestAuth = false, Settings = new Dictionary<string, string> { ["ApiAuth:CacheSeconds"] = "1" } };
        using var clientB = nodeB.CreateClient();
        (await BalanceAsync(clientB, first.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK); // node B now has the key cached
        (await BalanceAsync(clientB, second.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _app.PostAsync(Emergency(a.ClientId, $"api-keys/{first.Key.Id}/revoke"), new EmergencyRevokeRequest("leak"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.PutAsync(Emergency(a.ClientId, "api-access"), new SetApiAccessRequest(true, "kill"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await Task.Delay(TimeSpan.FromSeconds(1.5)); // > ApiAuth:CacheSeconds
        (await BalanceAsync(clientB, first.RawKey)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var killed = await BalanceAsync(clientB, second.RawKey);
        killed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(killed)).ShouldBe(ErrorCodes.ApiAccessDisabled);
    }

    [Fact]
    public async Task A_corrupt_client_ip_allow_list_denies_instead_of_allowing_everything()
    {
        var a = await NewTenantAsync("E5");
        var key = await CreateKeyAsync(a);
        await _app.WithDbAsync(async db =>
        {
            db.ClientSettings.Add(NexaVerify.Domain.Tenancy.ClientSetting.Create(a.ClientId, "integration.allowedIps", "this is { not json"));
            await db.SaveChangesAsync();
            return true;
        });

        var denied = await BalanceAsync(_app.Client, key.RawKey);

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(denied)).ShouldBe(ErrorCodes.IpNotAllowed);

        // repaired (an empty list = no restriction): access returns once the cache entry has aged out
        await _app.WithDbAsync(async db =>
        {
            (await db.ClientSettings.SingleAsync(x => x.ClientId == a.ClientId && x.Key == "integration.allowedIps")).SetValue("[]");
            await db.SaveChangesAsync();
            return true;
        });
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        (await BalanceAsync(_app.Client, key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Probing_unknown_key_prefixes_answers_uniformly_and_does_not_poison_real_keys()
    {
        var a = await NewTenantAsync("E6");
        var key = await CreateKeyAsync(a);

        for (var i = 0; i < 30; i++)
        {
            var guess = "nxv_live_" + Guid.NewGuid().ToString("N")[..8] + Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_') + new string('x', 20);
            var response = await BalanceAsync(_app.Client, guess);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await CodeOf(response)).ShouldBe(ErrorCodes.ApiKeyInvalid);
        }

        (await BalanceAsync(_app.Client, key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_emergency_endpoints_need_authentication_and_the_emergency_permission()
    {
        var a = await NewTenantAsync("E7");
        var key = await CreateKeyAsync(a);
        const string temp = "Temporary-Passphrase-55";
        (await _app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("NoEmergency", "Platform", null, [Permissions.Clients.Read]), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await _app.PostAsync("/api/v1/admin/users", new CreatePlatformUserRequest("noemergency@nexaverify.test", "No Emergency", temp, ["NoEmergency"]), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = await _app.LoginAsync("noemergency@nexaverify.test", temp);
        var staff = (await (await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(temp, "Granite-Lantern-Voyage-77"), first.AccessToken)).Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;

        var revokeAll = Emergency(a.ClientId, "api-keys/revoke-all");
        (await _app.PostAsync(revokeAll, new EmergencyRevokeRequest("x"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync(revokeAll, new EmergencyRevokeRequest("x"), a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // the client's own admin
        (await _app.PostAsync(revokeAll, new EmergencyRevokeRequest("x"), staff.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PutAsync(Emergency(a.ClientId, "api-access"), new SetApiAccessRequest(true, "x"), staff.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync(Emergency(a.ClientId, "api-access"), a.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync(Emergency(Guid.NewGuid(), "api-keys/revoke-all"), new EmergencyRevokeRequest("x"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PutAsync(Emergency(Guid.NewGuid(), "api-access"), new SetApiAccessRequest(true, "x"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await BalanceAsync(_app.Client, key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK); // nothing happened
    }
}
