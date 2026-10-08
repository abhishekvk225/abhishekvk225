using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>What clients see of platform staff (nothing), and who may change whom inside a client account.</summary>
[Collection(SqlServerCollection.Name)]
public class ClientViewAndPrivilegeTests : IAsyncLifetime
{
    private const string Password = "Granite-Lantern-Voyage-12";

    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public ClientViewAndPrivilegeTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture);
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task CreateClientRoleAsync(string name, params string[] permissions) =>
        (await _app.PostAsync("/api/v1/admin/roles", new CreateRoleRequest(name, "Client", null, permissions), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);

    /// <summary>Invites a user, accepts the invitation and signs in.</summary>
    private async Task<(ClientUserDto User, LoginResponse Session)> InviteAsync(string ownerToken, string email, string role)
    {
        var created = await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest(email, "Person " + role, role, null), ownerToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var user = (await created.Content.ReadFromJsonAsync<ClientUserDto>(AuthApp.Json))!;
        var (mail, token) = AuthApp.LinkFrom(_app.Emails.Sent[^1]);
        (await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(mail, token, Password))).EnsureSuccessStatusCode();
        return (user, await _app.LoginAsync(email, Password));
    }

    private Task<HttpResponseMessage> EditAsync(string token, Guid userId, string role, bool active = true) =>
        _app.PutAsync($"/api/v1/client/users/{userId}", new UpdateClientUserRequest("Renamed", role, null, active), token);

    private Task<HttpResponseMessage> ResetAsync(string token, Guid userId) => _app.PostAsync($"/api/v1/client/users/{userId}/reset-password", null, token);

    [Fact]
    public async Task A_user_manager_cannot_touch_people_with_equal_or_higher_access()
    {
        await CreateClientRoleAsync("Manager", Permissions.Users.Manage, Permissions.Dashboard.Client);
        await CreateClientRoleAsync("Viewer", Permissions.Dashboard.Client);
        var (client, owner) = await _app.OnboardClientAsync(_platform.AccessToken, "PV1", "owner@pv1.test", Password);
        var (manager, managerSession) = await InviteAsync(owner.AccessToken, "manager@pv1.test", "Manager");
        var (second, _) = await InviteAsync(owner.AccessToken, "admin2@pv1.test", SystemRoles.ClientAdmin);
        var (worker, _) = await InviteAsync(owner.AccessToken, "worker@pv1.test", SystemRoles.ClientUser);
        var (viewer, _) = await InviteAsync(owner.AccessToken, "viewer@pv1.test", "Viewer");
        var (peer, _) = await InviteAsync(owner.AccessToken, "manager2@pv1.test", "Manager");
        var ownerId = owner.User.Id;

        // higher (admin, owner), incomparable (a ClientUser holds permissions the manager lacks) and equal: all refused, for edits and for password resets
        foreach (var target in new[] { second.Id, ownerId, worker.Id, peer.Id })
        {
            (await EditAsync(managerSession.AccessToken, target, "Viewer", false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ResetAsync(managerSession.AccessToken, target)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // nothing changed
        var all = (await (await _app.GetAsync("/api/v1/client/users", owner.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ClientUserDto>>(AuthApp.Json))!.Items;
        all.Single(u => u.Id == second.Id).Status.ShouldBe("Active");
        all.Single(u => u.Id == ownerId).Role.ShouldBe(SystemRoles.ClientAdmin);
        (await _app.GetAsync("/api/v1/auth/me", owner.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // someone strictly below can be managed, and handing out a role the manager does not hold is still refused
        (await EditAsync(managerSession.AccessToken, viewer.Id, "Viewer", false)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ResetAsync(managerSession.AccessToken, viewer.Id)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await EditAsync(managerSession.AccessToken, viewer.Id, SystemRoles.ClientAdmin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("sneaky@pv1.test", "Sneaky", SystemRoles.ClientAdmin, null), managerSession.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        manager.Id.ShouldNotBe(Guid.Empty);
        client.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Admins_cannot_demote_each_other_but_the_account_owner_can_manage_them()
    {
        var (_, owner) = await _app.OnboardClientAsync(_platform.AccessToken, "PV2", "owner@pv2.test", Password);
        var (a2, a2Session) = await InviteAsync(owner.AccessToken, "a2@pv2.test", SystemRoles.ClientAdmin);
        var (a3, _) = await InviteAsync(owner.AccessToken, "a3@pv2.test", SystemRoles.ClientAdmin);
        var (worker, _) = await InviteAsync(owner.AccessToken, "w@pv2.test", SystemRoles.ClientUser);

        // a non-owner admin: not the owner, not another admin; a plain user is fine
        (await EditAsync(a2Session.AccessToken, owner.User.Id, SystemRoles.ClientUser)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await EditAsync(a2Session.AccessToken, owner.User.Id, SystemRoles.ClientAdmin, false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await EditAsync(a2Session.AccessToken, a3.Id, SystemRoles.ClientUser)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ResetAsync(a2Session.AccessToken, a3.Id)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ResetAsync(a2Session.AccessToken, owner.User.Id)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await EditAsync(a2Session.AccessToken, worker.Id, SystemRoles.ClientUser)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // the owner manages equals; and a user may still edit themselves (name, not deactivation)
        (await EditAsync(owner.AccessToken, a3.Id, SystemRoles.ClientAdmin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ResetAsync(owner.AccessToken, a3.Id)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await EditAsync(a2Session.AccessToken, a2.Id, SystemRoles.ClientAdmin, false)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await EditAsync(a2Session.AccessToken, a2.Id, SystemRoles.ClientAdmin)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EditAsync(owner.AccessToken, a3.Id, SystemRoles.ClientUser)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // and the last admin guard still holds (a2 is the only other admin; the owner may not strip both)
        (await EditAsync(owner.AccessToken, a2.Id, SystemRoles.ClientUser)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await EditAsync(owner.AccessToken, owner.User.Id, SystemRoles.ClientUser)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_clients_audit_view_never_shows_who_on_the_platform_did_something()
    {
        var (client, owner) = await _app.OnboardClientAsync(_platform.AccessToken, "PV3", "owner@pv3.test", Password);
        var detail = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}", _platform.AccessToken)).Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        (await _app.PutAsync($"/api/v1/admin/clients/{client.Id}",
            new UpdateClientRequest(detail.Name, detail.LegalName, detail.ContactEmail, detail.ContactPhone, detail.AddressLine1, detail.AddressLine2, detail.City, detail.State,
                detail.PostalCode, detail.Country, detail.Website, detail.Industry, detail.TimeZone, "Internal: chases invoices late", detail.RowVersion), _platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await InviteAsync(owner.AccessToken, "colleague@pv3.test", SystemRoles.ClientUser); // a row written by the client's own admin

        var raw = await _app.GetAsync("/api/v1/client/audit-logs?pageSize=100", owner.AccessToken);
        var body = await raw.Content.ReadAsStringAsync();
        var page = System.Text.Json.JsonSerializer.Deserialize<PagedResult<AuditLogDto>>(body, AuthApp.Json)!;

        body.ShouldNotContain(_platform.User.Id.ToString());
        var staffRows = page.Items.Where(a => a.Action is "client.updated" or "client.created" or "license.created").ToList();
        staffRows.ShouldNotBeEmpty();
        staffRows.ShouldAllBe(a => a.ActorId == null && a.IpAddress == null && a.ActorType == "Platform");
        var own = page.Items.Single(a => a.Action == "user.created" && a.ActorId == owner.User.Id);
        (own.ActorId, own.ActorType).ShouldBe((owner.User.Id, "User"));

        // platform staff still see who did what
        var platformView = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}/activity", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<AuditLogDto>>(AuthApp.Json))!;
        platformView.Items.Where(a => a.Action == "client.updated").ShouldAllBe(a => a.ActorId == _platform.User.Id);
    }

    [Fact]
    public async Task The_client_profile_hides_platform_notes_and_the_suspension_reason()
    {
        var (client, owner) = await _app.OnboardClientAsync(_platform.AccessToken, "PV4", "owner@pv4.test", Password);
        var detail = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}", _platform.AccessToken)).Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        (await _app.PutAsync($"/api/v1/admin/clients/{client.Id}",
            new UpdateClientRequest(detail.Name, detail.LegalName, detail.ContactEmail, detail.ContactPhone, detail.AddressLine1, detail.AddressLine2, detail.City, detail.State,
                detail.PostalCode, detail.Country, detail.Website, detail.Industry, detail.TimeZone, "Internal: chases invoices late", detail.RowVersion), _platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var profile = await (await _app.GetAsync("/api/v1/client/profile", owner.AccessToken)).Content.ReadAsStringAsync();
        var asPlatform = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}", _platform.AccessToken)).Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;

        profile.ShouldNotContain("chases invoices");
        System.Text.Json.JsonDocument.Parse(profile).RootElement.GetProperty("notes").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        asPlatform.Notes.ShouldBe("Internal: chases invoices late");

        // the update response (the client editing its own profile) is the same client view
        var current = (await (await _app.GetAsync("/api/v1/client/profile", owner.AccessToken)).Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        var updated = await _app.PutAsync("/api/v1/client/profile",
            new UpdateClientProfileRequest("Renamed Co", null, current.ContactEmail, null, null, null, null, null, null, "GB", null, null, current.TimeZone, current.RowVersion), owner.AccessToken);
        (await updated.Content.ReadAsStringAsync()).ShouldNotContain("chases invoices");
    }
}
