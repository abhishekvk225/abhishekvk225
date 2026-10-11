using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Application.Identity;
using NexaVerify.Domain.Identity;
using NexaVerify.Contracts.Identity;
using NexaVerify.Infrastructure.Platform.KeyRotation;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>
/// The running API with a master keyring: starts with two keys, serves encrypted data written under the old key, reports the keyring on
/// the health endpoint without secrets, survives an in-place rotation and keeps working when the old key is retired.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class MasterKeyRingTests
{
    private static readonly string KeyA = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string KeyB = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private readonly SqlServerFixture _fixture;

    public MasterKeyRingTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static Dictionary<string, string> Settings(string current, params (string Id, string Key)[] keys)
    {
        // a fresh JWT key per app instance: IdentityModel caches signature providers by key identity, and a disposed app's key must not be reused by the next
        var settings = new Dictionary<string, string> { ["Jwt:SigningKeyPem"] = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem(), ["Encryption:CurrentKeyId"] = current };
        for (var i = 0; i < keys.Length; i++)
        {
            settings[$"Encryption:Keys:{i}:Id"] = keys[i].Id;
            settings[$"Encryption:Keys:{i}:KeyBase64"] = keys[i].Key;
        }

        return settings;
    }

    private static async Task<JsonElement> KeyringHealthAsync(AuthApp app, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await app.Client.GetAsync("/health/keyring");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(expected, text);
        text.ShouldNotContain(KeyA);
        text.ShouldNotContain(KeyB);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    [Fact]
    public async Task The_app_runs_with_a_two_key_ring_and_the_health_endpoint_reports_it_without_secrets()
    {
        await using var app = await AuthApp.CreateAsync(_fixture, Settings("b", ("a", KeyA), ("b", KeyB)));
        var admin = await app.SuperAdminAsync();

        (await app.Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var health = await KeyringHealthAsync(app);
        health.GetProperty("status").GetString().ShouldBe("Healthy");
        health.GetProperty("currentKeyId").GetString().ShouldBe("b");
        health.GetProperty("keyIds").EnumerateArray().Select(e => e.GetString()).ShouldBe(["a", "b"]);
        health.GetProperty("unknownKeyIdsInUse").GetArrayLength().ShouldBe(0);

        // new tenant => its data key is wrapped under the current key
        var (client, tenantAdmin) = await app.OnboardClientAsync(admin.AccessToken, "KRING", "a@kring.test");
        var usage = await app.Factory.Services.GetRequiredService<KeyUsageSurvey>().SurveyAsync(default);
        usage.Under(KeyArtifacts.ClientDataKey, "b").ShouldBeGreaterThan(0);
        usage.Under(KeyArtifacts.ClientDataKey, "a").ShouldBe(0);
        client.Id.ShouldNotBe(Guid.Empty);
        tenantAdmin.AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Data_written_under_A_is_served_by_a_two_key_app_rotated_in_place_and_still_served_with_only_B()
    {
        // 1. the "old" deployment: only key A
        var connection = string.Empty;
        string mfaSecret;
        string staffSecret;
        const string staffEmail = "staff@kring.test";
        await using (var old = await AuthApp.CreateAsync(_fixture, Settings("a", ("a", KeyA))))
        {
            connection = old.ConnectionString;
            old.KeepDatabase = true;
            var platform = await old.SuperAdminAsync();
            var (client, tenantAdmin) = await old.OnboardClientAsync(platform.AccessToken, "KROT", "a@krot.test");
            await old.SeedLicenseAsync(client.Id, 100);
            (await EnrollFaceAsync(old, tenantAdmin.AccessToken, "emp-1", 1)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // a second enrolled staff member: a TOTP code is only accepted once per time step, so the "only B" phase needs a user whose step is unused
            const string temp = "Temporary-Passphrase-55";
            (await old.PostAsync("/api/v1/admin/users", new CreatePlatformUserRequest(staffEmail, "Staff", temp, [SystemRoles.SuperAdmin]), platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
            var first = await old.LoginAsync(staffEmail, temp);
            var changed = await ReadAsync<LoginResponse>(await old.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(temp, AuthApp.StrongPassword), first.AccessToken));
            var staffStart = await ReadAsync<MfaEnrolmentDto>(await old.PostAsync("/api/v1/auth/mfa/enroll", null, changed.AccessToken));
            staffSecret = staffStart.SecretBase32;
            (await old.PostAsync("/api/v1/auth/mfa/enroll/confirm", new ConfirmMfaRequest(Totp.Compute(Base32.Decode(staffSecret)!, Totp.StepAt(DateTime.UtcNow))), changed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // enabling MFA ends the admin's earlier sessions, so it comes last
            var start = await ReadAsync<MfaEnrolmentDto>(await old.PostAsync("/api/v1/auth/mfa/enroll", null, platform.AccessToken));
            mfaSecret = start.SecretBase32;
            (await old.PostAsync("/api/v1/auth/mfa/enroll/confirm", new ConfirmMfaRequest(Totp.Compute(Base32.Decode(mfaSecret)!, Totp.StepAt(DateTime.UtcNow))), platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // 2. new deployment with ring {a, b} current b over the same database: old data still works, the health endpoint says re-key is pending
        await using (var both = await AuthApp.CreateAsync(_fixture, Settings("b", ("a", KeyA), ("b", KeyB)), existingConnectionString: connection))
        {
            both.KeepDatabase = true;
            var pending = await KeyringHealthAsync(both);
            pending.GetProperty("status").GetString().ShouldBe("Degraded");
            pending.GetProperty("rewrapPending").GetBoolean().ShouldBeTrue();

            (await both.Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await VerifyFaceAsync(both, (await both.LoginAsync("a@krot.test", AuthApp.StrongPassword)).AccessToken, "emp-1", 1)).StatusCode.ShouldBe(HttpStatusCode.OK);
            await LoginWithMfaAsync(both, DatabaseBootstrap.SuperAdminEmail, mfaSecret);

            var report = await both.Factory.Services.GetRequiredService<MasterKeyRotationService>().RunAsync(new RotationRequest(false, 50), default);
            report.Outcome.ShouldBe(RotationOutcome.Complete, report.Message);
            report.ClientKeysRewrapped.ShouldBeGreaterThan(0);
            report.MfaSecretsReencrypted.ShouldBe(2);

            (await both.Factory.Services.GetRequiredService<KeyUsageSurvey>().SurveyAsync(default)).IsSafeToRetire("a").ShouldBeTrue();
        }

        // 3. A is retired: an app that has never seen A serves the same data
        await using var onlyB = await AuthApp.CreateAsync(_fixture, Settings("b", ("b", KeyB)), existingConnectionString: connection);
        (await VerifyFaceAsync(onlyB, (await onlyB.LoginAsync("a@krot.test", AuthApp.StrongPassword)).AccessToken, "emp-1", 1)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await LoginWithMfaAsync(onlyB, staffEmail, staffSecret);
        (await KeyringHealthAsync(onlyB)).GetProperty("status").GetString().ShouldBe("Healthy");
    }

    [Fact]
    public async Task An_app_missing_a_key_that_data_still_uses_is_not_ready_and_says_which_id()
    {
        string connection;
        await using (var old = await AuthApp.CreateAsync(_fixture, Settings("a", ("a", KeyA))))
        {
            connection = old.ConnectionString;
            old.KeepDatabase = true;
            var platform = await old.SuperAdminAsync();
            await old.OnboardClientAsync(platform.AccessToken, "KRMISS", "a@krmiss.test");
        }

        await using var wrong = await AuthApp.CreateAsync(_fixture, Settings("b", ("b", KeyB)), existingConnectionString: connection);

        (await wrong.Client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var health = await KeyringHealthAsync(wrong, HttpStatusCode.ServiceUnavailable);
        health.GetProperty("status").GetString().ShouldBe("Unhealthy");
        health.GetProperty("unknownKeyIdsInUse").EnumerateArray().Select(e => e.GetString()).ShouldBe(["a"], health.ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static Task<HttpResponseMessage> EnrollFaceAsync(AuthApp app, string token, string externalRef, int seed) =>
        app.PostFormAsync("/api/v1/faces/enroll", TestImages.Person(seed), new Dictionary<string, string> { ["externalRef"] = externalRef, ["consentReference"] = "consent-1" }, token);

    private static Task<HttpResponseMessage> VerifyFaceAsync(AuthApp app, string token, string externalRef, int seed) =>
        app.PostFormAsync("/api/v1/faces/verify", TestImages.Person(seed, variation: 3), new Dictionary<string, string> { ["externalRef"] = externalRef }, token);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(AuthApp.Json))!;
    }

    /// <summary>Password step, then the TOTP step with a code from a later time step than any used before.</summary>
    private static async Task LoginWithMfaAsync(AuthApp app, string email, string secret)
    {
        var step1 = await ReadAsync<LoginResponse>(await app.PostAsync("/api/v1/auth/login", new LoginRequest(email, AuthApp.StrongPassword)));
        step1.MfaRequired.ShouldBeTrue();
        var code = Totp.Compute(Base32.Decode(secret)!, Totp.StepAt(DateTime.UtcNow) + 1); // one step ahead of the enrolment step: inside the drift window, not a replay
        var done = await ReadAsync<LoginResponse>(await app.PostAsync("/api/v1/auth/mfa/verify", new VerifyMfaRequest(step1.MfaChallengeToken!, code)));
        done.AccessToken.ShouldNotBeNullOrEmpty();
    }
}
