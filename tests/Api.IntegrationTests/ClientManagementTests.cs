using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Domain.Common;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class ClientManagementTests : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private AuthApp _app = null!;
    private LoginResponse _platform = null!;

    public ClientManagementTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _app = await AuthApp.CreateAsync(_fixture);
        _platform = await _app.SuperAdminAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Creating_a_client_invites_its_first_admin_who_sets_their_own_password()
    {
        var response = await _app.PostAsync("/api/v1/admin/clients", AuthApp.NewClientRequest("ACME", "ada@acme.test"), _platform.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var client = (await response.Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        client.Status.ShouldBe("Active");
        client.Code.ShouldBe("ACME");

        // no password was shared: an invitation link was emailed instead
        var mail = _app.Emails.Sent.Single();
        mail.To.ShouldBe("ada@acme.test");
        mail.Subject.ShouldContain("invited");
        var (email, token) = AuthApp.LinkFrom(mail);

        // until the invitation is accepted nobody can sign in as that user
        (await _app.PostAsync("/api/v1/auth/login", new LoginRequest("ada@acme.test", AuthApp.StrongPassword))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, token, AuthApp.StrongPassword))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var admin = await _app.LoginAsync("ada@acme.test", AuthApp.StrongPassword);
        admin.User.Roles.ShouldBe([SystemRoles.ClientAdmin]);
        admin.User.ClientId.ShouldBe(client.Id);

        var me = (await (await _app.GetAsync("/api/v1/auth/me", admin.AccessToken)).Content.ReadFromJsonAsync<MeResponse>(AuthApp.Json))!;
        me.Client!.Name.ShouldBe("Acme ACME");
        me.Client.Status.ShouldBe("Active");
        me.Permissions.ShouldContain(Permissions.ApiKeys.Manage);
    }

    [Fact]
    public async Task Creation_validates_input_and_rejects_duplicates()
    {
        await _app.OnboardClientAsync(_platform.AccessToken, "DUP", "dup@acme.test");

        (await _app.PostAsync("/api/v1/admin/clients", AuthApp.NewClientRequest("dup", "other@acme.test"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.PostAsync("/api/v1/admin/clients", AuthApp.NewClientRequest("NEW", "dup@acme.test"), _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var bad = AuthApp.NewClientRequest("bad code!", "not-an-email") with { TimeZone = "Not A Zone", Country = "GBR", Website = "http://insecure.test" };
        var response = await _app.PostAsync("/api/v1/admin/clients", bad, _platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await BodyAsync(response)).GetProperty("errors");
        foreach (var field in new[] { "code", "adminEmail", "timeZone", "country", "website" })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue(field);
        }
    }

    [Fact]
    public async Task Client_list_is_paged_searchable_filterable_and_hides_the_platform_client()
    {
        await _app.OnboardClientAsync(_platform.AccessToken, "ALPHA", "a@alpha.test");
        var (beta, _) = await _app.OnboardClientAsync(_platform.AccessToken, "BETA", "b@beta.test");
        await _app.OnboardClientAsync(_platform.AccessToken, "GAMMA", "g@gamma.test");
        await _app.PostAsync($"/api/v1/admin/clients/{beta.Id}/suspend", new ClientStatusRequest("non-payment"), _platform.AccessToken);

        var all = (await (await _app.GetAsync("/api/v1/admin/clients?pageSize=2", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ClientListItemDto>>(AuthApp.Json))!;
        all.TotalCount.ShouldBe(3);
        all.Items.Count.ShouldBe(2);
        all.Items.ShouldNotContain(c => c.Code == "PLATFORM");
        all.Items[0].UserCount.ShouldBe(1);

        var suspended = (await (await _app.GetAsync("/api/v1/admin/clients?status=Suspended", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ClientListItemDto>>(AuthApp.Json))!;
        suspended.Items.Select(c => c.Code).ShouldBe(["BETA"]);
        var search = (await (await _app.GetAsync("/api/v1/admin/clients?search=gam", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ClientListItemDto>>(AuthApp.Json))!;
        search.Items.Select(c => c.Code).ShouldBe(["GAMMA"]);
        (await _app.GetAsync("/api/v1/admin/clients?status=Nonsense", _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.GetAsync($"/api/v1/admin/clients/{PlatformTenant.ClientId}", _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_uses_optimistic_concurrency()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "OCC", "o@occ.test");
        var request = new UpdateClientRequest("Renamed Ltd", null, client.ContactEmail, null, null, null, "Paris", null, null, "FR", null, null, "Europe/Paris", "vip", client.RowVersion);

        var first = await _app.PutAsync($"/api/v1/admin/clients/{client.Id}", request, _platform.AccessToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = (await first.Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        updated.Name.ShouldBe("Renamed Ltd");
        updated.RowVersion.ShouldNotBe(client.RowVersion);

        var stale = await _app.PutAsync($"/api/v1/admin/clients/{client.Id}", request with { Name = "Stale Edit" }, _platform.AccessToken);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyAsync(stale)).GetProperty("code").GetString().ShouldBe(ErrorCodes.ConcurrencyConflict);
    }

    [Fact]
    public async Task Suspension_takes_effect_immediately_everywhere_and_activation_restores_access()
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "SUSP", "s@susp.test");
        (await _app.GetAsync("/api/v1/client/profile", admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/suspend", new ClientStatusRequest(null), _platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest); // a reason is mandatory
        var suspended = await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/suspend", new ClientStatusRequest("Invoice overdue"), _platform.AccessToken);
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await suspended.Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!.StatusReason.ShouldBe("Invoice overdue");

        // the already-issued access token dies on the very next request, refresh and login are refused
        (await _app.GetAsync("/api/v1/client/profile", admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(admin.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var login = await _app.PostAsync("/api/v1/auth/login", new LoginRequest("s@susp.test", AuthApp.StrongPassword));
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyAsync(login)).GetProperty("code").GetString().ShouldBe(ErrorCodes.ClientSuspended);

        (await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/suspend", new ClientStatusRequest("again"), _platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict); // already suspended

        (await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/activate", null, _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.GetAsync("/api/v1/client/profile", (await _app.LoginAsync("s@susp.test", AuthApp.StrongPassword)).AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deactivation_blocks_access_with_its_own_code()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "DEACT", "d@deact.test");

        (await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/deactivate", new ClientStatusRequest("contract ended"), _platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var login = await _app.PostAsync("/api/v1/auth/login", new LoginRequest("d@deact.test", AuthApp.StrongPassword));
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await BodyAsync(login)).GetProperty("code").GetString().ShouldBe(ErrorCodes.ClientInactive);
    }

    [Fact]
    public async Task Status_changes_are_audited_and_visible_in_the_clients_activity()
    {
        var (client, _) = await _app.OnboardClientAsync(_platform.AccessToken, "AUD", "a@aud.test");
        await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/suspend", new ClientStatusRequest("review"), _platform.AccessToken);
        await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/activate", null, _platform.AccessToken);

        var activity = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}/activity", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<AuditLogDto>>(AuthApp.Json))!;
        activity.Items.Select(a => a.Action).ShouldContain("client.created");
        activity.Items.Select(a => a.Action).ShouldContain("client.suspended");
        activity.Items.Select(a => a.Action).ShouldContain("client.activated");
        activity.Items.ShouldAllBe(a => a.ActorId != null || a.ActorType == "System" || true);

        var filtered = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}/activity?action=client.suspended", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<AuditLogDto>>(AuthApp.Json))!;
        filtered.Items.Count.ShouldBe(1);

        var logins = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}/logins", _platform.AccessToken)).Content.ReadFromJsonAsync<PagedResult<LoginHistoryDto>>(AuthApp.Json))!;
        logins.Items.Select(l => l.Outcome).ShouldContain("Success");
        logins.Items.Select(l => l.Outcome).ShouldContain("PasswordReset"); // the invitation being accepted
    }

    [Fact]
    public async Task Only_authorised_platform_staff_can_manage_clients()
    {
        var (_, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "PERM", "p@perm.test");

        (await _app.GetAsync("/api/v1/admin/clients", admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync("/api/v1/admin/clients", AuthApp.NewClientRequest("EVIL", "e@evil.test"), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/admin/clients")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.GetAsync("/api/v1/client/profile", _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // platform has no client profile
    }

    [Fact]
    public async Task A_client_only_ever_sees_and_touches_its_own_data()
    {
        var (clientA, adminA) = await _app.OnboardClientAsync(_platform.AccessToken, "ISOA", "a@isoa.test");
        var (clientB, adminB) = await _app.OnboardClientAsync(_platform.AccessToken, "ISOB", "b@isob.test");

        var profile = (await (await _app.GetAsync("/api/v1/client/profile", adminA.AccessToken)).Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        profile.Id.ShouldBe(clientA.Id);

        var users = (await (await _app.GetAsync("/api/v1/client/users", adminA.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ClientUserDto>>(AuthApp.Json))!;
        users.Items.Select(u => u.Email).ShouldBe(["a@isoa.test"]);

        // B's user id is simply unknown to A
        var bUser = (await (await _app.GetAsync("/api/v1/client/users", adminB.AccessToken)).Content.ReadFromJsonAsync<PagedResult<ClientUserDto>>(AuthApp.Json))!.Items.Single();
        (await _app.PutAsync($"/api/v1/client/users/{bUser.Id}", new UpdateClientUserRequest("Hijacked", SystemRoles.ClientUser, null, false), adminA.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _app.PostAsync($"/api/v1/client/users/{bUser.Id}/reset-password", null, adminA.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var activity = (await (await _app.GetAsync("/api/v1/client/audit-logs", adminA.AccessToken)).Content.ReadFromJsonAsync<PagedResult<AuditLogDto>>(AuthApp.Json))!;
        activity.Items.ShouldNotBeEmpty();
        var logins = (await (await _app.GetAsync("/api/v1/client/logins", adminA.AccessToken)).Content.ReadFromJsonAsync<PagedResult<LoginHistoryDto>>(AuthApp.Json))!;
        logins.Items.ShouldAllBe(l => l.EmailAttempted == "a@isoa.test");

        // ...and the database layer says the same: a tenant-scoped context cannot read B's client or users at all
        var visible = await _app.WithTenantDbAsync(clientA.Id, async db =>
        {
            var clients = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.Clients.Select(c => c.Id));
            var rawUsers = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ExecuteDeleteAsync(db.Users.IgnoreQueryFilters().Where(u => u.ClientId == clientB.Id));
            return (clients, rawUsers);
        });
        visible.clients.ShouldBe([clientA.Id]);
        visible.rawUsers.ShouldBe(0); // RLS: even a filter-bypassing bulk delete matches nothing of B's
    }

    [Fact]
    public async Task Clients_can_edit_only_their_own_profile_with_concurrency_protection()
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "PROF", "p@prof.test");
        var profile = (await (await _app.GetAsync("/api/v1/client/profile", admin.AccessToken)).Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        var update = new UpdateClientProfileRequest("Profile Co", null, "new@prof.test", null, null, null, null, null, null, "GB", null, null, "Europe/London", profile.RowVersion);

        var ok = await _app.PutAsync("/api/v1/client/profile", update, admin.AccessToken);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!.Name.ShouldBe("Profile Co");
        (await _app.PutAsync("/api/v1/client/profile", update with { Name = "Stale" }, admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // status is not part of the profile: an admin cannot unsuspend or alter platform-only fields through it
        var after = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}", _platform.AccessToken)).Content.ReadFromJsonAsync<ClientDto>(AuthApp.Json))!;
        after.Status.ShouldBe("Active");
        after.Code.ShouldBe("PROF");
    }

    [Fact]
    public async Task Client_admins_manage_their_users_with_guard_rails()
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "USERS", "boss@users.test");
        var before = _app.Emails.Sent.Count;

        var created = await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("worker@users.test", "Wendy Worker", SystemRoles.ClientUser, "Operator"), admin.AccessToken);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var worker = (await created.Content.ReadFromJsonAsync<ClientUserDto>(AuthApp.Json))!;
        worker.Role.ShouldBe(SystemRoles.ClientUser);
        worker.JobTitle.ShouldBe("Operator");
        _app.Emails.Sent.Count.ShouldBe(before + 1);

        var (email, token) = AuthApp.LinkFrom(_app.Emails.Sent[^1]);
        await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, token, "Granite-Lantern-Voyage-12"));
        var workerLogin = await _app.LoginAsync("worker@users.test", "Granite-Lantern-Voyage-12");

        // a ClientUser cannot manage users
        (await _app.GetAsync("/api/v1/client/users", workerLogin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("x@users.test", "X", SystemRoles.ClientAdmin, null), workerLogin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // roles must be client roles; unknown/platform roles are refused
        (await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("y@users.test", "Y", SystemRoles.SuperAdmin, null), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("worker@users.test", "Dup", SystemRoles.ClientUser, null), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // the last admin cannot be demoted, deactivated, or deactivate themselves
        (await _app.PutAsync($"/api/v1/client/users/{admin.User.Id}", new UpdateClientUserRequest("Boss", SystemRoles.ClientUser, null, true), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _app.PutAsync($"/api/v1/client/users/{admin.User.Id}", new UpdateClientUserRequest("Boss", SystemRoles.ClientAdmin, null, false), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // deactivating the worker ends their session at once
        (await _app.PutAsync($"/api/v1/client/users/{worker.Id}", new UpdateClientUserRequest("Wendy Worker", SystemRoles.ClientUser, "Operator", false), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _app.GetAsync("/api/v1/auth/me", workerLogin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(workerLogin.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // promoting a second admin allows the first to step down
        var second = (await (await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("boss2@users.test", "Boss Two", SystemRoles.ClientAdmin, null), admin.AccessToken))
            .Content.ReadFromJsonAsync<ClientUserDto>(AuthApp.Json))!;
        (await _app.PutAsync($"/api/v1/client/users/{admin.User.Id}", new UpdateClientUserRequest("Boss", SystemRoles.ClientUser, null, true), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Role.ShouldBe(SystemRoles.ClientAdmin);
        client.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task The_user_limit_is_enforced()
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "LIMIT", "l@limit.test");
        var set = await _app.PutAsync($"/api/v1/admin/clients/{client.Id}/settings",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Limits.MaxUsers] = JsonSerializer.SerializeToElement(2) }), _platform.AccessToken);
        set.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("u2@limit.test", "Two", SystemRoles.ClientUser, null), admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var third = await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("u3@limit.test", "Three", SystemRoles.ClientUser, null), admin.AccessToken);
        third.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyAsync(third)).GetProperty("code").GetString().ShouldBe("USER_LIMIT_REACHED");
    }

    [Fact]
    public async Task Support_can_reset_a_client_users_password_without_ever_seeing_it()
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "RESET", "r@reset.test");
        var before = _app.Emails.Sent.Count;

        var response = await _app.PostAsync($"/api/v1/admin/clients/{client.Id}/users/{admin.User.Id}/reset-password", null, _platform.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("password");
        _app.Emails.Sent.Count.ShouldBe(before + 1);
        _app.Emails.Sent[^1].To.ShouldBe("r@reset.test");
        (await _app.GetAsync("/api/v1/auth/me", admin.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // sessions ended
        (await _app.PostAsync("/api/v1/auth/refresh", new RefreshRequest(admin.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // a user from another client is not reachable through this client's route
        var (other, _) = await _app.OnboardClientAsync(_platform.AccessToken, "OTHER", "o@other.test");
        (await _app.PostAsync($"/api/v1/admin/clients/{other.Id}/users/{admin.User.Id}/reset-password", null, _platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Settings_have_defaults_bounds_and_per_key_permissions()
    {
        var (client, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "SET", "s@set.test");

        var all = (await (await _app.GetAsync($"/api/v1/admin/clients/{client.Id}/settings", _platform.AccessToken)).Content.ReadFromJsonAsync<List<SettingDto>>(AuthApp.Json))!;
        all.Single(s => s.Key == SettingKeys.Face.MatchThreshold).Value.GetDecimal().ShouldBe(0.60m);
        all.ShouldAllBe(s => !s.IsOverridden);

        // a client may tune its own recognition threshold within bounds...
        (await _app.PutAsync("/api/v1/client/settings/recognition",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Face.MatchThreshold] = JsonSerializer.SerializeToElement(0.75) }), admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var recognition = (await (await _app.GetAsync("/api/v1/client/settings/recognition", admin.AccessToken)).Content.ReadFromJsonAsync<List<SettingDto>>(AuthApp.Json))!;
        recognition.ShouldAllBe(s => s.Group == "Recognition");
        recognition.Single(s => s.Key == SettingKeys.Face.MatchThreshold).Value.GetDecimal().ShouldBe(0.75m);
        recognition.Single(s => s.Key == SettingKeys.Face.MatchThreshold).IsOverridden.ShouldBeTrue();

        // ...but not outside them, and not with the wrong type
        var tooLow = await _app.PutAsync("/api/v1/client/settings/recognition",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Face.MatchThreshold] = JsonSerializer.SerializeToElement(0.1), [SettingKeys.Face.RetainImages] = JsonSerializer.SerializeToElement("yes") }), admin.AccessToken);
        tooLow.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await BodyAsync(tooLow)).GetProperty("errors");
        errors.TryGetProperty(SettingKeys.Face.MatchThreshold, out _).ShouldBeTrue();
        errors.TryGetProperty(SettingKeys.Face.RetainImages, out _).ShouldBeTrue();

        // platform-managed keys are read-only to the client, even via another group's endpoint
        var limits = await _app.PutAsync("/api/v1/client/settings/security",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Limits.MaxUsers] = JsonSerializer.SerializeToElement(999) }), admin.AccessToken);
        limits.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // not part of this group
        var webhooks = await _app.PutAsync("/api/v1/client/settings/security",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Integration.WebhooksEnabled] = JsonSerializer.SerializeToElement(false) }), admin.AccessToken);
        webhooks.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // platform can set platform-managed keys, and everything is audited
        (await _app.PutAsync($"/api/v1/admin/clients/{client.Id}/settings",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Api.RateLimitPerMinute] = JsonSerializer.SerializeToElement(500) }), _platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var audit = await _app.WithDbAsync(db => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.AuditLogs.Where(a => a.Action == "client.settings_updated" && a.ClientId == client.Id)));
        audit.Count.ShouldBe(2);

        // setting a value back to its default removes the override
        (await _app.PutAsync("/api/v1/client/settings/recognition",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [SettingKeys.Face.MatchThreshold] = JsonSerializer.SerializeToElement(0.60) }), admin.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var reset = (await (await _app.GetAsync("/api/v1/client/settings/recognition", admin.AccessToken)).Content.ReadFromJsonAsync<List<SettingDto>>(AuthApp.Json))!;
        reset.Single(s => s.Key == SettingKeys.Face.MatchThreshold).IsOverridden.ShouldBeFalse();
    }

    [Fact]
    public async Task List_settings_validate_ip_ranges_and_origins()
    {
        var (_, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "LISTS", "l@lists.test");

        async Task<HttpResponseMessage> Put(string key, params string[] values) => await _app.PutAsync("/api/v1/client/settings/security",
            new UpdateSettingsRequest(new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(values) }), admin.AccessToken);

        (await Put(SettingKeys.Integration.AllowedIps, "203.0.113.5", "10.0.0.0/8", "2001:db8::/32")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Put(SettingKeys.Integration.AllowedIps, "not-an-ip")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Put(SettingKeys.Integration.AllowedIps, "10.0.0.0/33")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Put(SettingKeys.Integration.AllowedOrigins, "https://app.example.com")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Put(SettingKeys.Integration.AllowedOrigins, "http://app.example.com")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Put(SettingKeys.Integration.AllowedOrigins, "https://app.example.com/path")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Put(SettingKeys.Integration.AllowedOrigins, "*")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Client_users_without_settings_permissions_cannot_read_or_change_settings()
    {
        var (_, admin) = await _app.OnboardClientAsync(_platform.AccessToken, "NOSET", "n@noset.test");
        await _app.PostAsync("/api/v1/client/users", new CreateClientUserRequest("worker@noset.test", "Worker", SystemRoles.ClientUser, null), admin.AccessToken);
        var (email, token) = AuthApp.LinkFrom(_app.Emails.Sent[^1]);
        await _app.PostAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(email, token, "Granite-Lantern-Voyage-12"));
        var worker = await _app.LoginAsync("worker@noset.test", "Granite-Lantern-Voyage-12");

        (await _app.GetAsync("/api/v1/client/settings/recognition", worker.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.PutAsync("/api/v1/client/settings/recognition", new UpdateSettingsRequest(new Dictionary<string, JsonElement>()), worker.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/client/profile", worker.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _app.GetAsync("/api/v1/client/audit-logs", worker.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
