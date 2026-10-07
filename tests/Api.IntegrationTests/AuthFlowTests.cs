using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Identity;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class AuthFlowTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;

    public AuthFlowTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync() => _app = await AuthApp.CreateAsync(_fixture);

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Seeded_super_admin_must_change_password_before_using_any_permissioned_endpoint()
    {
        var login = await _app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);

        login.MustChangePassword.ShouldBeTrue();
        login.User.IsPlatformUser.ShouldBeTrue();
        login.User.Roles.ShouldBe([SystemRoles.SuperAdmin]);
        (await _app.GetAsync("/api/v1/admin/roles", login.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/auth/me", login.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var changed = await _app.PostAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(DatabaseBootstrap.SuperAdminPassword, AuthApp.StrongPassword), login.AccessToken);
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fresh = (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
        fresh.MustChangePassword.ShouldBeFalse();
        (await _app.GetAsync("/api/v1/admin/roles", fresh.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // the pre-change token and the old password are dead
        (await _app.GetAsync("/api/v1/auth/me", login.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync("/api/v1/auth/login", new LoginRequest(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Access_token_carries_the_expected_claims_and_no_permissions()
    {
        var admin = await _app.SuperAdminAsync();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(admin.AccessToken);

        token.Header.Alg.ShouldBe("ES256");
        token.Claims.Single(c => c.Type == NexaClaims.ActorType).Value.ShouldBe(NexaClaims.PlatformActor);
        token.Claims.Any(c => c.Type == NexaClaims.ClientId).ShouldBeFalse();
        token.Claims.Any(c => c.Type is "perm" or "permissions" or "scope").ShouldBeFalse();
        (token.ValidTo - token.ValidFrom).TotalMinutes.ShouldBe(15, 0.1);
        admin.AccessToken.ShouldNotContain(DatabaseBootstrap.SuperAdminPassword);
    }

    [Fact]
    public async Task Me_returns_profile_roles_and_permissions()
    {
        var admin = await _app.SuperAdminAsync();

        var me = (await (await _app.GetAsync("/api/v1/auth/me", admin.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;

        me.User.Email.ShouldBe(DatabaseBootstrap.SuperAdminEmail);
        me.Permissions.ShouldContain(Permissions.Licenses.Create);
        me.Permissions.ShouldNotContain(Permissions.Faces.Identify); // client scope: never granted to platform roles
    }

    [Fact]
    public async Task Bad_password_and_unknown_email_are_indistinguishable_and_recorded()
    {
        await _app.SuperAdminAsync();

        var wrongPassword = await _app.PostAsync("/api/v1/auth/login", new LoginRequest(DatabaseBootstrap.SuperAdminEmail, "Wrong-Passphrase-1"));
        var unknownEmail = await _app.PostAsync("/api/v1/auth/login", new LoginRequest("nobody@nowhere.test", "Wrong-Passphrase-1"));

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownEmail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var a = await ProblemAsync(wrongPassword);
        var b = await ProblemAsync(unknownEmail);
        a.GetProperty("code").GetString().ShouldBe(b.GetProperty("code").GetString());
        a.GetProperty("detail").GetString().ShouldBe(b.GetProperty("detail").GetString());

        var outcomes = await _app.WithDbAsync(db => db.LoginHistory.OrderBy(h => h.Id).Select(h => new { h.Outcome, h.EmailAttempted }).ToListAsync());
        outcomes.Count(o => o.Outcome == LoginOutcome.InvalidCredentials).ShouldBe(2);
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_even_for_the_right_password()
    {
        var clientId = Guid.NewGuid();
        await _app.CreateClientUserAsync(clientId, "locked@client.test", SystemRoles.ClientAdmin);

        for (var i = 0; i < 5; i++)
        {
            (await _app.PostAsync("/api/v1/auth/login", new LoginRequest("locked@client.test", "Not-The-Password-1"))).StatusCode
                .ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await _app.PostAsync("/api/v1/auth/login", new LoginRequest("locked@client.test", AuthApp.StrongPassword))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        var history = await _app.WithDbAsync(db => db.LoginHistory.Where(h => h.ClientId == clientId).Select(h => h.Outcome).ToListAsync());
        history.ShouldContain(LoginOutcome.LockedOut);
    }

    [Fact]
    public async Task Refresh_rotates_and_reuse_of_a_rotated_token_revokes_the_whole_family()
    {
        var first = await _app.SuperAdminAsync();
        var login = await _app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, AuthApp.StrongPassword);

        var rotated = await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken));
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = (await rotated.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
        second.RefreshToken.ShouldNotBe(login.RefreshToken);
        (await _app.GetAsync("/api/v1/auth/me", second.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // an attacker replays the old, already-rotated token
        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // ...which burns the legitimate holder's newest token too
        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var audit = await _app.WithDbAsync(db => db.AuditLogs.Select(a => a.Action).ToListAsync());
        audit.ShouldContain("session.refresh_reuse_detected");
        first.RefreshToken.ShouldNotBeNull();
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token_family()
    {
        var admin = await _app.SuperAdminAsync();

        (await _app.PostAsync("/api/v1/auth/logout", new LogoutRequest(admin.RefreshToken), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(admin.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forgot_password_answers_identically_for_known_and_unknown_emails_and_reset_is_single_use()
    {
        var admin = await _app.SuperAdminAsync();

        var unknown = await _app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("ghost@nowhere.test"));
        var known = await _app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(DatabaseBootstrap.SuperAdminEmail));
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        known.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await unknown.Content.ReadAsStringAsync()).ShouldBe(await known.Content.ReadAsStringAsync());

        _app.Emails.Sent.Count.ShouldBe(1);
        var mail = _app.Emails.Sent[0];
        mail.To.ShouldBe(DatabaseBootstrap.SuperAdminEmail);
        var link = new Uri(mail.Body.Split('\n').Single(l => l.StartsWith("https://", StringComparison.Ordinal)));
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        var token = query["token"]!;
        query["email"].ShouldBe(DatabaseBootstrap.SuperAdminEmail);

        const string newPassword = "A-Brand-New-Passphrase-77";
        var reset = await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(DatabaseBootstrap.SuperAdminEmail, token, newPassword));
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(DatabaseBootstrap.SuperAdminEmail, token, "Another-Passphrase-88")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.GetAsync("/api/v1/auth/me", admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // old sessions die
        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(admin.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, newPassword)).MustChangePassword.ShouldBeFalse();
    }

    [Fact]
    public async Task Reset_with_a_wrong_token_or_a_weak_password_is_rejected()
    {
        await _app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(DatabaseBootstrap.SuperAdminEmail));

        var wrong = await _app.PostAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(DatabaseBootstrap.SuperAdminEmail, "not-the-token", "A-Brand-New-Passphrase-77"));
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var weak = await _app.PostAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(DatabaseBootstrap.SuperAdminEmail, "whatever", "password"));
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(weak)).GetProperty("errors").GetProperty("newPassword").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("password1234")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    public async Task Change_password_enforces_the_password_policy(string weak)
    {
        var login = await _app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);

        var response = await _app.PostAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(DatabaseBootstrap.SuperAdminPassword, weak), login.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Change_password_with_a_wrong_current_password_fails_without_changing_anything()
    {
        var login = await _app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);

        var response = await _app.PostAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest("Wrong-Current-Passphrase-1", AuthApp.StrongPassword), login.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.GetAsync("/api/v1/auth/me", login.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Tampered_unsigned_and_foreign_signed_tokens_are_rejected()
    {
        var admin = await _app.SuperAdminAsync();
        var parts = admin.AccessToken.Split('.');

        // 1) payload tampering keeps the signature but breaks it
        var payload = System.Text.Encoding.UTF8.GetString(Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(parts[1]));
        var forged = payload.Replace("\"platform\"", "\"platform\"").Replace("\"sv\":1", "\"sv\":1") + " ";
        var tampered = $"{parts[0]}.{Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(forged))}.{parts[2]}";
        (await _app.GetAsync("/api/v1/auth/me", tampered)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // 2) alg=none
        var none = $"{Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray())}.{parts[1]}.";
        (await _app.GetAsync("/api/v1/auth/me", none)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // 3) validly signed by somebody else's key
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var foreign = handler.CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "nexaverify",
            Audience = "nexaverify-api",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { [NexaClaims.Subject] = Guid.NewGuid().ToString(), [NexaClaims.SecurityVersion] = 1, [NexaClaims.ActorType] = NexaClaims.PlatformActor, [NexaClaims.Role] = new[] { SystemRoles.SuperAdmin } },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.ECDsaSecurityKey(other) { KeyId = "k1" }, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.EcdsaSha256),
        });
        (await _app.GetAsync("/api/v1/admin/roles", foreign)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Expired_and_wrong_audience_tokens_are_rejected()
    {
        var keys = _app.Factory.Services.GetRequiredService<NexaVerify.Infrastructure.Identity.JwtKeyProvider>();
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();

        string Make(string audience, DateTime expires) => handler.CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "nexaverify",
            Audience = audience,
            NotBefore = expires.AddMinutes(-10),
            Expires = expires,
            Claims = new Dictionary<string, object> { [NexaClaims.Subject] = Guid.NewGuid().ToString(), [NexaClaims.SecurityVersion] = 1 },
            SigningCredentials = keys.SigningCredentials,
        });

        (await _app.GetAsync("/api/v1/auth/me", Make("nexaverify-api", DateTime.UtcNow.AddMinutes(-5)))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.GetAsync("/api/v1/auth/me", Make("someone-else", DateTime.UtcNow.AddMinutes(5)))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Client_roles_cannot_reach_platform_endpoints_and_platform_cannot_use_client_permissions()
    {
        var clientId = Guid.NewGuid();
        await _app.CreateClientUserAsync(clientId, "boss@client.test", SystemRoles.ClientAdmin);
        var client = await _app.LoginAsync("boss@client.test", AuthApp.StrongPassword);

        client.User.ClientId.ShouldBe(clientId);
        client.User.IsPlatformUser.ShouldBeFalse();
        (await _app.GetAsync("/api/v1/admin/roles", client.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/admin/users", client.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/admin/permissions", client.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(client.AccessToken);
        token.Claims.Single(c => c.Type == NexaClaims.ClientId).Value.ShouldBe(clientId.ToString());
        token.Claims.Any(c => c.Type == NexaClaims.ActorType).ShouldBeFalse();
    }

    [Fact]
    public async Task Users_see_only_their_own_tenant_through_the_application_layer()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var userA = await _app.CreateClientUserAsync(a, "a@client.test", SystemRoles.ClientAdmin);
        await _app.CreateClientUserAsync(b, "b@client.test", SystemRoles.ClientAdmin);

        var visibleToA = await _app.WithDbAsync(async db =>
        {
            // exactly what a request for tenant A would see: switch the ambient scope to A
            return await db.Users.IgnoreQueryFilters().CountAsync();
        });
        visibleToA.ShouldBeGreaterThanOrEqualTo(3); // platform scope sees seed admin + both clients' users

        var login = await _app.LoginAsync("a@client.test", AuthApp.StrongPassword);
        var me = (await (await _app.GetAsync("/api/v1/auth/me", login.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;
        me.User.Id.ShouldBe(userA);
        me.Permissions.ShouldContain(Permissions.Faces.Identify);
        me.Permissions.ShouldNotContain(Permissions.Clients.Create);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_sessions_immediately()
    {
        var admin = await _app.SuperAdminAsync();
        var create = await _app.PostAsync("/api/v1/admin/users",
            new CreatePlatformUserRequest("support@nexaverify.test", "Support Person", "Temporary-Passphrase-55", [SystemRoles.SuperAdmin]), admin.AccessToken);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await create.Content.ReadFromJsonAsync<PlatformUserDto>(AuthApp.Json))!;
        created.MustChangePassword.ShouldBeTrue();

        // the new staff member changes the temporary password and gets to work
        var first = await _app.LoginAsync("support@nexaverify.test", "Temporary-Passphrase-55");
        var changed = await _app.PostAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest("Temporary-Passphrase-55", "Support-Own-Passphrase-66"), first.AccessToken);
        var support = (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
        (await _app.GetAsync("/api/v1/admin/roles", support.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var deactivate = await _app.PutAsync($"/api/v1/admin/users/{created.Id}",
            new UpdatePlatformUserRequest("Support Person", [SystemRoles.SuperAdmin], false), admin.AccessToken);
        deactivate.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _app.GetAsync("/api/v1/admin/roles", support.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(support.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync("/api/v1/auth/login", new LoginRequest("support@nexaverify.test", "Support-Own-Passphrase-66"))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Liveness_and_readiness_pass_on_a_fully_guarded_database()
    {
        (await _app.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited_per_ip()
    {
        await using var limited = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["RateLimiting:AuthPerIpPerMinute"] = "3" });
        for (var i = 0; i < 3; i++)
        {
            (await limited.PostAsync("/api/v1/auth/login", new LoginRequest("x@y.test", "Whatever-Passphrase-1"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var blocked = await limited.PostAsync("/api/v1/auth/login", new LoginRequest("x@y.test", "Whatever-Passphrase-1"));
        blocked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ProblemAsync(blocked)).GetProperty("code").GetString().ShouldBe(ErrorCodes.RateLimited);
    }

    [Fact]
    public async Task Login_request_validation_returns_field_errors()
    {
        var response = await _app.PostAsync("/api/v1/auth/login", new { email = "not-an-email", password = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await ProblemAsync(response)).GetProperty("errors");
        errors.TryGetProperty("email", out _).ShouldBeTrue();
        errors.TryGetProperty("password", out _).ShouldBeTrue();
    }
}
