using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class RbacTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _admin = null!;

    public RbacTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture);
        _admin = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<RoleDto> CreateRoleAsync(string name, string scope, params string[] permissions)
    {
        var response = await _app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest(name, scope, null, permissions), _admin.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleDto>(AuthApp.Json))!;
    }

    private async Task<LoginResponse> StaffWithRoleAsync(string email, string role)
    {
        const string temp = "Temporary-Passphrase-55";
        (await _app.PostAsync("/api/v1/admin/users", new CreatePlatformUserRequest(email, "Staff " + role, temp, [role]), _admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = await _app.LoginAsync(email, temp);
        var changed = await _app.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(temp, "Granite-Lantern-Voyage-77"), first.AccessToken);
        return (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
    }

    [Fact]
    public async Task System_roles_have_the_documented_permission_sets()
    {
        var roles = (await (await _app.GetAsync("/api/v1/admin/roles", _admin.AccessToken)).Content.ReadFromJsonAsync<List<RoleDto>>(AuthApp.Json))!;

        roles.Select(r => r.Name).ShouldBe([SystemRoles.ClientAdmin, SystemRoles.ClientUser, SystemRoles.SuperAdmin], ignoreOrder: true);
        roles.ShouldAllBe(r => r.IsSystem);
        var superAdmin = roles.Single(r => r.Name == SystemRoles.SuperAdmin);
        superAdmin.Permissions.ShouldContain(Permissions.Licenses.Create);
        superAdmin.Permissions.ShouldNotContain(Permissions.Faces.Enroll);
        var clientUser = roles.Single(r => r.Name == SystemRoles.ClientUser);
        clientUser.Permissions.ShouldBe(SystemRoles.ClientUserPermissions, ignoreOrder: true);
        clientUser.Permissions.ShouldNotContain(Permissions.ApiKeys.Manage);
    }

    [Fact]
    public async Task Permission_catalogue_is_synced_from_code()
    {
        var permissions = (await (await _app.GetAsync("/api/v1/admin/permissions", _admin.AccessToken)).Content.ReadFromJsonAsync<List<PermissionDto>>(AuthApp.Json))!;

        permissions.Select(p => p.Key).ShouldBe(Permissions.All.Select(p => p.Key), ignoreOrder: true);
    }

    [Fact]
    public async Task A_new_role_is_just_data_and_takes_effect_for_its_members()
    {
        await CreateRoleAsync("Billing Viewer", "Platform", Permissions.Licenses.Read);
        var staff = await StaffWithRoleAsync("billing@nexaverify.test", "Billing Viewer");

        var me = (await (await _app.GetAsync("/api/v1/auth/me", staff.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;
        me.Permissions.ShouldBe([Permissions.Licenses.Read]);
        (await _app.GetAsync("/api/v1/admin/roles", staff.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // lacks roles.manage
    }

    [Fact]
    public async Task Changing_a_roles_permissions_applies_immediately_without_relogin()
    {
        var role = await CreateRoleAsync("Role Manager", "Platform", Permissions.RolesAdmin.Manage);
        var staff = await StaffWithRoleAsync("rolemgr@nexaverify.test", "Role Manager");
        (await _app.GetAsync("/api/v1/admin/permissions", staff.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var update = await _app.PutAsync($"/api/v1/admin/roles/{role.Id}", new UpdateRoleRequest("now powerless", [Permissions.Licenses.Read]), _admin.AccessToken);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        // removed permissions apply at once: the holder's token is ended, and after signing in again the permission is gone
        (await _app.GetAsync("/api/v1/admin/permissions", staff.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var again = await _app.LoginAsync("rolemgr@nexaverify.test", "Granite-Lantern-Voyage-77");
        (await _app.GetAsync("/api/v1/admin/permissions", again.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Nobody_can_grant_permissions_they_do_not_hold()
    {
        await CreateRoleAsync("Role Only Manager", "Platform", Permissions.RolesAdmin.Manage);
        var staff = await StaffWithRoleAsync("roleonly@nexaverify.test", "Role Only Manager");

        var escalate = await _app.PostAsync("/api/v1/admin/roles",
            new CreateRoleRequest("Sneaky", "Platform", null, [Permissions.Licenses.Create]), staff.AccessToken);

        escalate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await escalate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain(Permissions.Licenses.Create);
    }

    [Fact]
    public async Task Staff_cannot_be_given_a_role_more_powerful_than_their_creator_holds()
    {
        await CreateRoleAsync("Users Manager", "Platform", Permissions.Users.PlatformManage);
        var staff = await StaffWithRoleAsync("usersmgr@nexaverify.test", "Users Manager");

        var response = await _app.PostAsync("/api/v1/admin/users",
            new CreatePlatformUserRequest("boss@nexaverify.test", "Would-be boss", "Temporary-Passphrase-55", [SystemRoles.SuperAdmin]), staff.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Platform", "faces.identify")] // client permission on a platform role
    [InlineData("Client", "licenses.create")] // platform permission on a client role
    [InlineData("Platform", "no.such.permission")]
    public async Task Invalid_permission_grants_are_rejected(string scope, string permission)
    {
        var response = await _app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("Bad Role", scope, null, [permission]), _admin.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Role_names_are_unique_and_system_roles_are_immutable()
    {
        await CreateRoleAsync("Auditor", "Platform", Permissions.Audit.Read);

        (await _app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest("auditor", "Platform", null, []), _admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var roles = (await (await _app.GetAsync("/api/v1/admin/roles", _admin.AccessToken)).Content.ReadFromJsonAsync<List<RoleDto>>(AuthApp.Json))!;
        var system = roles.First(r => r.IsSystem);
        var update = await _app.PutAsync($"/api/v1/admin/roles/{system.Id}", new UpdateRoleRequest("hack", []), _admin.AccessToken);
        update.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("ROLE_IMMUTABLE");
    }

    [Fact]
    public async Task Platform_users_enforce_policy_uniqueness_scope_and_self_protection()
    {
        (await _app.PostAsync("/api/v1/admin/users",
            new CreatePlatformUserRequest("weak@nexaverify.test", "Weak", "short", [SystemRoles.SuperAdmin]), _admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _app.PostAsync("/api/v1/admin/users",
            new CreatePlatformUserRequest(DatabaseBootstrap.SuperAdminEmail, "Dup", "Temporary-Passphrase-55", [SystemRoles.SuperAdmin]), _admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await _app.PostAsync("/api/v1/admin/users",
            new CreatePlatformUserRequest("clientish@nexaverify.test", "Client role", "Temporary-Passphrase-55", [SystemRoles.ClientAdmin]), _admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _app.PostAsync("/api/v1/admin/users",
            new CreatePlatformUserRequest("norole@nexaverify.test", "No role", "Temporary-Passphrase-55", ["Ghost"]), _admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var me = (await (await _app.GetAsync("/api/v1/auth/me", _admin.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;
        (await _app.PutAsync($"/api/v1/admin/users/{me.User.Id}", new UpdatePlatformUserRequest("Me", [SystemRoles.SuperAdmin], false), _admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Platform_user_list_is_paged_searchable_and_excludes_client_users()
    {
        await _app.CreateClientUserAsync(Guid.NewGuid(), "client@client.test", SystemRoles.ClientAdmin);
        for (var i = 0; i < 3; i++)
        {
            await StaffWithRoleAsync($"staff{i}@nexaverify.test", SystemRoles.SuperAdmin);
        }

        var all = (await (await _app.GetAsync("/api/v1/admin/users?pageSize=2&page=1", _admin.AccessToken)).Content.ReadFromJsonAsync<PagedResult<PlatformUserDto>>(AuthApp.Json))!;
        all.TotalCount.ShouldBe(4);
        all.Items.Count.ShouldBe(2);
        all.TotalPages.ShouldBe(2);

        var found = (await (await _app.GetAsync("/api/v1/admin/users?search=staff1", _admin.AccessToken)).Content.ReadFromJsonAsync<PagedResult<PlatformUserDto>>(AuthApp.Json))!;
        found.Items.Select(u => u.Email).ShouldBe(["staff1@nexaverify.test"]);

        var injection = await _app.GetAsync("/api/v1/admin/users?search=%25%27%3B--", _admin.AccessToken);
        injection.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Audit_trail_records_rbac_and_user_changes_without_secrets()
    {
        var role = await CreateRoleAsync("Audited Role", "Platform", Permissions.Audit.Read);
        await StaffWithRoleAsync("audited@nexaverify.test", "Audited Role");
        await _app.PutAsync($"/api/v1/admin/roles/{role.Id}", new UpdateRoleRequest("changed", [Permissions.Audit.Read, Permissions.Reports.Read]), _admin.AccessToken);

        var rows = await _app.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.AuditLogs.OrderBy(a => a.Id)));

        rows.Select(r => r.Action).ShouldContain("role.created");
        rows.Select(r => r.Action).ShouldContain("role.updated");
        rows.Select(r => r.Action).ShouldContain("user.created");
        rows.Select(r => r.Action).ShouldContain("user.password_changed");
        var serialized = string.Join('\n', rows.Select(r => r.NewValuesJson + r.OldValuesJson));
        serialized.ShouldNotContain("Temporary-Passphrase");
        serialized.ShouldNotContain("Staff-Own-Passphrase");
        rows.ShouldAllBe(r => r.OccurredAt > DateTime.UtcNow.AddMinutes(-5) && r.CorrelationId != null);
    }
}
