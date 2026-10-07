using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Identity;
using NexaVerify.Domain.Identity;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Regression tests for the findings of the M2 security review (each was reproduced before being fixed).</summary>
[Collection(SqlServerCollection.Name)]
public class SecurityHardeningTests
{
    private const string StaffPassword = "Granite-Lantern-Voyage-77";
    private readonly SqlServerFixture _fixture;

    public SecurityHardeningTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<LoginResponse> NewStaffAsync(AuthApp app, LoginResponse admin, string email, string role)
    {
        const string temp = "Temporary-Passphrase-55";
        (await app.PostAsync("/api/v1/admin/users", new CreatePlatformUserRequest(email, "Staff Member", temp, [role]), admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = await app.LoginAsync(email, temp);
        var changed = await app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(temp, StaffPassword), first.AccessToken);
        return (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
    }

    [Fact]
    public async Task H1_a_burst_of_parallel_wrong_passwords_cannot_out_run_the_lockout()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        await app.CreateClientUserAsync(Guid.NewGuid(), "victim@client.test", SystemRoles.ClientAdmin);

        var burst = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            app.PostAsync("/api/v1/auth/login", new LoginRequest("victim@client.test", "Not-The-Password-1"))));

        burst.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized);
        // the account is locked: the right password is refused too
        (await app.PostAsync("/api/v1/auth/login", new LoginRequest("victim@client.test", AuthApp.StrongPassword))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var state = await app.WithDbAsync(db => db.Users.Where(u => u.Email == "victim@client.test").Select(u => new { u.LockoutEnd }).SingleAsync());
        state.LockoutEnd.ShouldNotBeNull();
        var outcomes = await app.WithDbAsync(db => db.LoginHistory.Where(h => h.EmailAttempted == "victim@client.test").Select(h => h.Outcome).ToListAsync());
        outcomes.Count(o => o == LoginOutcome.InvalidCredentials).ShouldBeLessThanOrEqualTo(5); // at most the permitted attempts were really evaluated
    }

    [Fact]
    public async Task M3_a_lockout_never_ends_live_sessions_or_blocks_password_reset()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var clientId = Guid.NewGuid();
        await app.CreateClientUserAsync(clientId, "target@client.test", SystemRoles.ClientAdmin);
        var live = await app.LoginAsync("target@client.test", AuthApp.StrongPassword);

        for (var i = 0; i < 7; i++)
        {
            await app.PostAsync("/api/v1/auth/login", new LoginRequest("target@client.test", "Not-The-Password-1"));
        }

        // an attacker locking the account does not log the real user out...
        (await app.GetAsync("/api/v1/auth/me", live.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(live.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...and the emailed reset link is the way out of the lockout
        (await app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("target@client.test"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var (email, token) = AuthApp.LinkFrom(app.Emails.Sent[^1]);
        const string fresh = "Marble-Orchard-Skyline-31";
        (await app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, token, fresh))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.LoginAsync("target@client.test", fresh)).User.ClientId.ShouldBe(clientId);
    }

    [Fact]
    public async Task M4_change_password_is_subject_to_the_same_lockout()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var login = (await app.SuperAdminAsync());

        for (var i = 0; i < 6; i++)
        {
            (await app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest("Wrong-Current-Passphrase-1", "Another-Valid-Passphrase-9"), login.AccessToken))
                .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // the correct current password is now refused as well: a stolen token cannot brute-force it
        var response = await app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(AuthApp.StrongPassword, "Another-Valid-Passphrase-9"), login.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("locked");
    }

    [Fact]
    public async Task M1_parallel_refreshes_with_one_token_produce_exactly_one_successor()
    {
        // the production default grace window: late arrivals of a double-submit are refused, not treated as theft
        await using var app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Auth:RefreshReuseGraceSeconds"] = "10" });
        var login = await app.SuperAdminAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken))));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var tokens = (await winner.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
        var live = await app.WithDbAsync(db => db.RefreshTokens.CountAsync(t => t.UserId == tokens.User.Id && t.RevokedAt == null));
        live.ShouldBe(1); // no forked family: only the winner's token is live
        (await app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(tokens.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task M1_a_double_submit_within_the_grace_window_is_refused_without_killing_the_session()
    {
        await using var app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Auth:RefreshReuseGraceSeconds"] = "60" });
        var login = await app.SuperAdminAsync();
        var rotated = (await (await app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken))).Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;

        (await app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(login.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task H2_a_delegated_admin_cannot_modify_or_demote_a_more_privileged_user()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var admin = await app.SuperAdminAsync();
        (await app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Users Mgr", "Platform", null, [Permissions.Users.PlatformManage]), admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var delegated = await NewStaffAsync(app, admin, "delegate@nexaverify.test", "Users Mgr");
        var me = (await (await app.GetAsync("/api/v1/auth/me", admin.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;

        var attack = await app.PutAsync($"/api/v1/admin/users/{me.User.Id}",
            new UpdatePlatformUserRequest("Pwned", ["Users Mgr"], false), delegated.AccessToken);

        attack.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.GetAsync("/api/v1/admin/roles", admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK); // the Super Admin is untouched
    }

    [Fact]
    public async Task H2_the_last_active_super_admin_cannot_be_demoted_or_deactivated()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var admin = await app.SuperAdminAsync();
        (await app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Viewer", "Platform", null, [Permissions.Clients.Read]), admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var me = (await (await app.GetAsync("/api/v1/auth/me", admin.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;

        var selfDemote = await app.PutAsync($"/api/v1/admin/users/{me.User.Id}", new UpdatePlatformUserRequest("Root", ["Viewer"], true), admin.AccessToken);
        selfDemote.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await selfDemote.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("LAST_SUPER_ADMIN");

        // with a second Super Admin, the first may step down
        var second = await NewStaffAsync(app, admin, "second@nexaverify.test", SystemRoles.SuperAdmin);
        (await app.PutAsync($"/api/v1/admin/users/{me.User.Id}", new UpdatePlatformUserRequest("Root", ["Viewer"], true), second.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondMe = (await (await app.GetAsync("/api/v1/auth/me", second.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;
        (await app.PutAsync($"/api/v1/admin/users/{secondMe.User.Id}", new UpdatePlatformUserRequest("Second", ["Viewer"], true), second.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict); // now the only one left
    }

    [Fact]
    public async Task M7_roles_cannot_be_edited_by_someone_who_does_not_hold_all_of_their_permissions()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var admin = await app.SuperAdminAsync();
        (await app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Role Editor", "Platform", null, [Permissions.RolesAdmin.Manage]), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var finance = (await (await app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Finance", "Platform", null, [Permissions.Licenses.Read, Permissions.Licenses.Adjust]), admin.AccessToken))
            .Content.ReadFromJsonAsync<RoleDto>(AuthApp.Json))!;
        var editor = await NewStaffAsync(app, admin, "editor@nexaverify.test", "Role Editor");

        var response = await app.PutAsync($"/api/v1/admin/roles/{finance.Id}", new UpdateRoleRequest("emptied", []), editor.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Role_update_keeps_unchanged_permissions_and_swaps_the_rest()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var admin = await app.SuperAdminAsync();
        var role = (await (await app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Swapper", "Platform", null, [Permissions.Licenses.Read, Permissions.Reports.Read]), admin.AccessToken))
            .Content.ReadFromJsonAsync<RoleDto>(AuthApp.Json))!;

        var updated = (await (await app.PutAsync($"/api/v1/admin/roles/{role.Id}", new UpdateRoleRequest("swapped", [Permissions.Licenses.Read, Permissions.Audit.Read]), admin.AccessToken))
            .Content.ReadFromJsonAsync<RoleDto>(AuthApp.Json))!;

        updated.Permissions.ShouldBe([Permissions.Audit.Read, Permissions.Licenses.Read], ignoreOrder: true);
    }

    [Fact]
    public async Task Null_or_oversized_list_elements_are_validation_errors_not_server_errors()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var admin = await app.SuperAdminAsync();

        var nullPermission = await app.PostAsync("/api/v1/admin/roles", JsonDocument.Parse("""{"name":"N","scope":"Platform","description":null,"permissions":[null]}""").RootElement, admin.AccessToken);
        var nullRole = await app.PostAsync("/api/v1/admin/users", JsonDocument.Parse("""{"email":"n@n.test","fullName":"N","temporaryPassword":"Temporary-Passphrase-55","roles":[null]}""").RootElement, admin.AccessToken);
        var longRole = await app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Long", "Platform", null, [new string('x', 500)]), admin.AccessToken);
        var badScope = await app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Scoped", "Galaxy", null, []), admin.AccessToken);

        foreach (var response in new[] { nullPermission, nullRole, longRole, badScope })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task M2_forgot_password_cooldown_prevents_email_bombing_and_responses_take_constant_time()
    {
        await using var app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string>
        {
            ["Auth:PasswordResetCooldownSeconds"] = "300",
            ["Auth:SensitiveResponseMinimumMilliseconds"] = "400",
        });

        var timer = Stopwatch.StartNew();
        (await app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(DatabaseBootstrap.SuperAdminEmail))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var known = timer.ElapsedMilliseconds;
        timer.Restart();
        (await app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("ghost@nowhere.test"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var unknown = timer.ElapsedMilliseconds;
        (await app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(DatabaseBootstrap.SuperAdminEmail))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await app.PostAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(DatabaseBootstrap.SuperAdminEmail))).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        known.ShouldBeGreaterThanOrEqualTo(380);
        unknown.ShouldBeGreaterThanOrEqualTo(380);
        app.Emails.Sent.Count.ShouldBe(1); // three requests, one email
    }

    [Fact]
    public async Task Unknown_login_emails_are_stored_only_as_a_fingerprint()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);

        await app.PostAsync("/api/v1/auth/login", new LoginRequest("typo.in.name@nowhere.test", "whatever-Passphrase-1"));

        var rows = await app.WithDbAsync(db => db.LoginHistory.Select(h => h.EmailAttempted).ToListAsync());
        rows.ShouldAllBe(r => !r.Contains("typo.in.name"));
        rows.ShouldContain(r => r.StartsWith("unknown:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Change_password_rejects_passwords_derived_from_the_users_email()
    {
        await using var app = await AuthApp.CreateAsync(_fixture);
        var login = await app.LoginAsync(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword);

        var response = await app.PostAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(DatabaseBootstrap.SuperAdminPassword, "ROOT-is-my-favourite-98"), login.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("newPassword", out _).ShouldBeTrue();
    }

    [Fact]
    public void Production_refuses_development_only_switches()
    {
        using var ephemeral = new ApiFactory
        {
            Environment = "Production",
            Settings = new Dictionary<string, string>
            {
                ["Hosting:RedirectToHttps"] = "false",
                ["AllowedHosts"] = "api.example.com",
                ["Email:LogBodies"] = "true",
            },
        };

        Should.Throw<InvalidOperationException>(() => ephemeral.CreateClient()).Message.ShouldContain("Email:LogBodies");
    }

    [Fact]
    public async Task Readiness_fails_when_the_app_login_could_switch_off_row_level_security()
    {
        await using var app = await AuthApp.CreateAsync(_fixture, new Dictionary<string, string> { ["Database:RequireLeastPrivilege"] = "true" });

        // the tests connect as sa, which can drop the policy
        (await app.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Readiness_passes_with_the_least_privilege_principal_and_the_api_works_as_that_login()
    {
        var adminConnection = await _fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(adminConnection);
        const string login = "nexaverify_app";
        const string password = "App-Login-Passphrase-0123456789!";
        await DatabaseBootstrap.CreateApplicationPrincipalAsync(adminConnection, login, password);

        var appConnection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(adminConnection) { UserID = login, Password = password }.ConnectionString;
        var emails = new CapturingEmailSender();
        await using var factory = new ApiFactory
        {
            ConnectionString = appConnection,
            UseTestAuth = false,
            Settings = new Dictionary<string, string> { ["Database:RequireLeastPrivilege"] = "true" },
            ConfigureServices = s => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<NexaVerify.Application.Abstractions.IEmailOutbox>(s, emails),
        };
        var client = factory.CreateClient();

        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(DatabaseBootstrap.SuperAdminEmail, DatabaseBootstrap.SuperAdminPassword));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_application_principal_cannot_disable_row_level_security_or_rewrite_history()
    {
        var adminConnection = await _fixture.CreateDatabaseAsync();
        await DatabaseBootstrap.MigrateAsync(adminConnection);
        await DatabaseBootstrap.CreateApplicationPrincipalAsync(adminConnection, "nexaverify_app2", "App-Login-Passphrase-0123456789!");
        var appConnection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(adminConnection) { UserID = "nexaverify_app2", Password = "App-Login-Passphrase-0123456789!" }.ConnectionString;

        foreach (var sql in new[]
        {
            "DROP SECURITY POLICY [security].[TenantPolicy]",
            "ALTER SECURITY POLICY [security].[TenantPolicy] WITH (STATE = OFF)",
            "DELETE FROM [audit].[AuditLogs]",
            "UPDATE [iam].[LoginHistory] SET Outcome = 'Success'",
            "DISABLE TRIGGER ALL ON [audit].[AuditLogs]",
            "CREATE TABLE dbo.Evil (id int)",
        })
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(appConnection);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await Should.ThrowAsync<Microsoft.Data.SqlClient.SqlException>(() => command.ExecuteNonQueryAsync(), sql);
        }
    }

    [Fact]
    public async Task Migrations_can_be_re_run_on_an_already_guarded_database()
    {
        var connectionString = await _fixture.CreateDatabaseAsync();

        await DatabaseBootstrap.MigrateAsync(connectionString);
        await DatabaseBootstrap.MigrateAsync(connectionString); // second run: nothing to migrate, guards re-installed atomically, seed idempotent

        await using var factory = new ApiFactory { ConnectionString = connectionString };
        (await factory.CreateClient().GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
