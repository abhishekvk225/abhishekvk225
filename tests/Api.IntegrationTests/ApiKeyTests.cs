using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Infrastructure.Background;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>API keys end to end: issuing, authenticating, scopes, revocation, rotation, throttling and the request log.</summary>
[Collection(SqlServerCollection.Name)]
public class ApiKeyTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public ApiKeyTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture);
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

    private async Task<CreatedApiKeyDto> CreateKeyAsync(Tenant t, string name = "ci", string[]? scopes = null, int? rate = null, string[]? ips = null, DateTime? expires = null)
    {
        var response = await _app.PostAsync("/api/v1/client/api-keys",
            new CreateApiKeyRequest(name, scopes ?? ["faces.read", "faces.verify", "faces.enroll", "faces.identify"], expires, rate, ips), t.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!;
    }

    private Task<HttpResponseMessage> WithKeyAsync(HttpMethod method, string path, string? key, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (key is not null)
        {
            request.Headers.Add("X-Api-Key", key);
        }

        return _app.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> BalanceAsync(string? key) => WithKeyAsync(HttpMethod.Get, "/api/v1/faces/balance", key);

    private static async Task<string?> CodeOf(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("code").GetString();

    [Fact]
    public async Task A_new_key_is_shown_once_and_only_its_hash_is_stored()
    {
        var t = await NewTenantAsync("K1");
        var created = await CreateKeyAsync(t);

        created.RawKey.ShouldStartWith("nxv_live_");
        created.RawKey.Length.ShouldBe(61);
        created.Key.Prefix.ShouldBe(created.RawKey[..17]);

        var list = await (await _app.GetAsync("/api/v1/client/api-keys", t.Token)).Content.ReadAsStringAsync();
        list.ShouldNotContain(created.RawKey);
        list.ShouldNotContain(created.RawKey[18..]);
        list.ShouldNotContain("keyHash", Case.Insensitive);

        var stored = await _app.WithTenantDbAsync(t.ClientId, db => db.ApiKeys.AsNoTracking().SingleAsync());
        stored.KeyHash.Length.ShouldBe(32);
        stored.KeyHash.ShouldBe(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(created.RawKey)));
    }

    [Fact]
    public async Task A_key_authenticates_and_is_limited_to_its_scopes()
    {
        var t = await NewTenantAsync("K2");
        var key = await CreateKeyAsync(t, scopes: ["faces.read"]);

        var ok = await BalanceAsync(key.RawKey);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await ok.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("remaining").GetInt32().ShouldBe(100);

        // not granted verify/enroll
        var form = new MultipartFormDataContent { { new ByteArrayContent(TestImages.Person(1)), "image", "a.jpg" } };
        (await WithKeyAsync(HttpMethod.Post, "/api/v1/faces/identify", key.RawKey, form)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // a key can never manage keys, users or licences
        (await WithKeyAsync(HttpMethod.Get, "/api/v1/client/api-keys", key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await WithKeyAsync(HttpMethod.Get, "/api/v1/client/licenses/summary", key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_full_recognition_flow_works_with_just_an_api_key()
    {
        var t = await NewTenantAsync("K3");
        var key = await CreateKeyAsync(t);

        async Task<HttpResponseMessage> Post(string path, byte[] image, Dictionary<string, string> fields)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(image), "image", "a.jpg" } };
            foreach (var (k, v) in fields)
            {
                form.Add(new StringContent(v), k);
            }

            return await WithKeyAsync(HttpMethod.Post, path, key.RawKey, form);
        }

        (await Post("/api/v1/faces/enroll", TestImages.Person(7), new() { ["externalRef"] = "e1", ["consentReference"] = "c1" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var verify = await Post("/api/v1/faces/verify", TestImages.Person(7, 1), new() { ["externalRef"] = "e1" });
        (await verify.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("match").GetBoolean().ShouldBeTrue();

        // the request is attributed to the key, not to a user
        var row = await _app.WithTenantDbAsync(t.ClientId, db => db.RecognitionRequests.AsNoTracking().OrderBy(r => r.CreatedAt).FirstAsync());
        row.ApiKeyId.ShouldBe(key.Key.Id);
        row.UserId.ShouldBeNull();
    }

    [Fact]
    public async Task Wrong_unknown_and_malformed_keys_all_get_the_same_answer()
    {
        var t = await NewTenantAsync("K4");
        var key = await CreateKeyAsync(t);

        var wrongSecret = key.RawKey[..18] + new string('A', 43);
        var unknown = "nxv_live_zzzzzzzz_" + new string('A', 43);
        foreach (var attempt in new[] { wrongSecret, unknown, "garbage", "nxv_live_short", new string('x', 61) })
        {
            var response = await BalanceAsync(attempt);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await CodeOf(response)).ShouldBe(ErrorCodes.ApiKeyInvalid);
        }

        (await BalanceAsync(null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_key_stops_it_immediately_and_cannot_be_repeated()
    {
        var t = await NewTenantAsync("K5");
        var key = await CreateKeyAsync(t);
        (await BalanceAsync(key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK); // warms the key cache

        (await _app.PostAsync($"/api/v1/client/api-keys/{key.Key.Id}/revoke", new RevokeApiKeyRequest("leaked"), t.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await BalanceAsync(key.RawKey);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(after)).ShouldBe(ErrorCodes.ApiKeyRevoked);
        (await _app.PostAsync($"/api/v1/client/api-keys/{key.Key.Id}/revoke", new RevokeApiKeyRequest("again"), t.Token)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.WithDbAsync(db => db.AuditLogs.AnyAsync(a => a.Action == "apikey.revoked" && a.ClientId == t.ClientId))).ShouldBeTrue();
    }

    [Fact]
    public async Task An_expired_key_is_refused_with_its_own_code()
    {
        var t = await NewTenantAsync("K6");
        var key = await CreateKeyAsync(t, expires: DateTime.UtcNow.AddHours(1));
        await _app.WithTenantDbAsync(t.ClientId, async db =>
        {
            await db.ApiKeys.Where(k => k.Id == key.Key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            return true;
        });

        var response = await BalanceAsync(key.RawKey);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(response)).ShouldBe(ErrorCodes.ApiKeyExpired);
        (await ListKeysAsync(t)).Single().EffectiveStatus.ShouldBe("Expired");
    }

    private async Task<List<ApiKeyDto>> ListKeysAsync(Tenant t) =>
        (await (await _app.GetAsync("/api/v1/client/api-keys", t.Token)).Content.ReadFromJsonAsync<List<ApiKeyDto>>(AuthApp.Json))!;

    [Fact]
    public async Task An_ip_allow_list_restricts_where_a_key_can_be_used_from()
    {
        var t = await NewTenantAsync("K7");
        var key = await CreateKeyAsync(t, ips: ["203.0.113.0/24"]);

        var response = await BalanceAsync(key.RawKey);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(response)).ShouldBe(ErrorCodes.IpNotAllowed);

        (await _app.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("bad", ["faces.read"], null, null, ["not-an-ip"]), t.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Only_integration_scopes_the_creator_holds_can_be_granted()
    {
        var t = await NewTenantAsync("K8");

        foreach (var scope in new[] { "apikeys.manage", "licenses.read", "settings.recognition", "nonsense" })
        {
            var response = await _app.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("x", [scope], null, null, null), t.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, scope);
        }

        // a ClientUser cannot create keys at all
        await _app.CreateClientUserAsync(t.ClientId, "u@k8.test", "ClientUser");
        var user = await _app.LoginAsync("u@k8.test", AuthApp.StrongPassword);
        (await _app.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("x", ["faces.read"], null, null, null), user.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Regenerating_replaces_the_key_with_an_optional_grace_period()
    {
        var t = await NewTenantAsync("K9");
        var old = await CreateKeyAsync(t, name: "prod");

        var replaced = await _app.PostAsync($"/api/v1/client/api-keys/{old.Key.Id}/regenerate", new RegenerateApiKeyRequest(0), t.Token);
        var fresh = (await replaced.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!;
        fresh.Key.RotatedFromKeyId.ShouldBe(old.Key.Id);
        fresh.Key.Scopes.ShouldBe(old.Key.Scopes);
        (await BalanceAsync(old.RawKey)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await BalanceAsync(fresh.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var graceful = await _app.PostAsync($"/api/v1/client/api-keys/{fresh.Key.Id}/regenerate", new RegenerateApiKeyRequest(60), t.Token);
        var third = (await graceful.Content.ReadFromJsonAsync<CreatedApiKeyDto>(AuthApp.Json))!;
        (await BalanceAsync(fresh.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK); // still within its grace period
        (await BalanceAsync(third.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_key_is_throttled_per_minute_and_other_keys_are_unaffected()
    {
        var t = await NewTenantAsync("K10");
        var slow = await CreateKeyAsync(t, name: "slow", rate: 3);
        var other = await CreateKeyAsync(t, name: "other");

        for (var i = 0; i < 3; i++)
        {
            (await BalanceAsync(slow.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await BalanceAsync(slow.RawKey);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await CodeOf(limited)).ShouldBe(ErrorCodes.RateLimited);
        (await BalanceAsync(other.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_daily_quota_is_per_client_and_set_by_the_platform()
    {
        var a = await NewTenantAsync("K11A");
        var b = await NewTenantAsync("K11B");
        var keyA = await CreateKeyAsync(a);
        var keyB = await CreateKeyAsync(b);

        var set = await _app.PutAsync($"/api/v1/admin/clients/{a.ClientId}/settings",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Api.DailyQuota] = JsonSerializer.SerializeToElement(2) }), _platform.AccessToken);
        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());

        (await BalanceAsync(keyA.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BalanceAsync(keyA.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var over = await BalanceAsync(keyA.RawKey);
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeOf(over)).ShouldBe(ErrorCodes.DailyQuotaExceeded);
        (await BalanceAsync(keyB.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK); // another client is unaffected
    }

    [Fact]
    public async Task A_suspended_clients_keys_stop_working()
    {
        var t = await NewTenantAsync("K12");
        var key = await CreateKeyAsync(t);
        (await BalanceAsync(key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _app.PostAsync($"/api/v1/admin/clients/{t.ClientId}/suspend", new ClientStatusRequest("non-payment"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await BalanceAsync(key.RawKey);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(response)).ShouldBe(ErrorCodes.ClientSuspended);
    }

    [Fact]
    public async Task Keys_are_private_to_their_client_and_capped_in_number()
    {
        var a = await NewTenantAsync("K13A");
        var b = await NewTenantAsync("K13B");
        var keyA = await CreateKeyAsync(a);

        (await _app.GetAsync($"/api/v1/client/api-keys/{keyA.Key.Id}", b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PostAsync($"/api/v1/client/api-keys/{keyA.Key.Id}/revoke", new RevokeApiKeyRequest("x"), b.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ListKeysAsync(b)).ShouldBeEmpty();

        // A's key sees only A's data
        await _app.PostFormAsync("/api/v1/faces/enroll", TestImages.Person(3), new Dictionary<string, string> { ["externalRef"] = "e", ["consentReference"] = "c" }, b.Token);
        var profiles = await WithKeyAsync(HttpMethod.Get, "/api/v1/faces/profiles", keyA.RawKey);
        (await profiles.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json)).GetProperty("totalCount").GetInt32().ShouldBe(0);

        // default limit is 5 active keys
        for (var i = 0; i < 4; i++)
        {
            await CreateKeyAsync(a, name: "k" + i);
        }

        var sixth = await _app.PostAsync("/api/v1/client/api-keys", new CreateApiKeyRequest("six", ["faces.read"], null, null, null), a.Token);
        sixth.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(sixth)).ShouldBe("APIKEY_LIMIT_REACHED");
    }

    [Fact]
    public async Task Updates_need_the_current_version_and_never_reach_a_revoked_key()
    {
        var t = await NewTenantAsync("K14");
        var key = await CreateKeyAsync(t);

        var stale = await _app.PutAsync($"/api/v1/client/api-keys/{key.Key.Id}",
            new UpdateApiKeyRequest("renamed", ["faces.read"], null, null, null, Convert.ToBase64String(new byte[8])), t.Token);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var ok = await _app.PutAsync($"/api/v1/client/api-keys/{key.Key.Id}",
            new UpdateApiKeyRequest("renamed", ["faces.read"], null, 10, null, key.Key.RowVersion), t.Token);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await BalanceAsync(key.RawKey)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var identify = new MultipartFormDataContent { { new ByteArrayContent(TestImages.Person(1)), "image", "a.jpg" } };
        (await WithKeyAsync(HttpMethod.Post, "/api/v1/faces/identify", key.RawKey, identify)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // scope was narrowed at once

        var fresh = (await ListKeysAsync(t)).Single();
        await _app.PostAsync($"/api/v1/client/api-keys/{key.Key.Id}/revoke", new RevokeApiKeyRequest("done"), t.Token);
        (await _app.PutAsync($"/api/v1/client/api-keys/{key.Key.Id}", new UpdateApiKeyRequest("x", ["faces.read"], null, null, null, fresh.RowVersion), t.Token))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Calls_are_logged_by_route_template_without_content_and_last_use_is_recorded()
    {
        var t = await NewTenantAsync("K15");
        var key = await CreateKeyAsync(t);
        await BalanceAsync(key.RawKey);
        await WithKeyAsync(HttpMethod.Get, "/api/v1/faces/profiles/" + Guid.NewGuid(), key.RawKey);
        await BalanceAsync("nxv_live_zzzzzzzz_" + new string('A', 43)); // unauthenticated: not attributable to a client, so not logged

        await _app.Factory.Services.GetRequiredService<ApiRequestLogWriter>().FlushAsync(default);

        var page = await JsonOf(await _app.GetAsync($"/api/v1/client/api-logs?apiKeyId={key.Key.Id}", t.Token));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items.Select(i => i.GetProperty("route").GetString()).ShouldContain("api/v1/faces/balance");
        items.Select(i => i.GetProperty("route").GetString()).ShouldContain("api/v1/faces/profiles/{id:guid}");
        items.Single(i => i.GetProperty("statusCode").GetInt32() == 404).GetProperty("errorCode").GetString().ShouldBe(ErrorCodes.NotFound);
        page.GetRawText().ShouldNotContain(key.RawKey);

        var errors = await JsonOf(await _app.GetAsync("/api/v1/client/api-logs?statusClass=ClientError", t.Token));
        errors.GetProperty("totalCount").GetInt32().ShouldBe(1);

        (await ListKeysAsync(t)).Single().LastUsedAt.ShouldNotBeNull();
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(AuthApp.Json);
}
