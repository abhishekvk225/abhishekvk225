using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexaVerify.Api.IntegrationTests.Support;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Identity;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Domain.Licensing;
using NexaVerify.TestSupport;

namespace NexaVerify.Api.IntegrationTests;

/// <summary>Two-person approval for credit adjustments above <c>Licensing:MaxAdjustPerAction</c>.</summary>
[Collection(SqlServerCollection.Name)]
public class LicenseAdjustmentApprovalTests : UsageTestBase
{
    private const string Password = "Granite-Lantern-Voyage-77";

    public LicenseAdjustmentApprovalTests(SqlServerFixture fixture)
        : base(fixture)
    {
    }

    protected override IReadOnlyDictionary<string, string>? Settings => new Dictionary<string, string>
    {
        ["Licensing:MaxAdjustPerAction"] = "100",
        ["Licensing:AdjustApprovalHours"] = "24",
    };

    private async Task<LoginResponse> StaffAsync(string email, string role)
    {
        const string temp = "Temporary-Passphrase-55";
        (await App.PostAsync("/api/v1/admin/users", new CreatePlatformUserRequest(email, "Staff " + role, temp, [role]), Platform.AccessToken))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = await App.LoginAsync(email, temp);
        var changed = await App.PostAsync("/api/v1/auth/change-password", new ChangePasswordRequest(temp, Password), first.AccessToken);
        return (await changed.Content.ReadFromJsonAsync<LoginResponse>(AuthApp.Json))!;
    }

    private async Task<LoginResponse> StaffWithPermissionsAsync(string email, string roleName, params string[] permissions)
    {
        (await App.PostAsync("/api/v1/admin/roles", new CreateRoleRequest(roleName, "Platform", null, permissions), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return await StaffAsync(email, roleName);
    }

    private static string Adjust(Guid license) => $"/api/v1/admin/licenses/{license}/adjust";

    private static string Decide(Guid request, string verb) => $"/api/v1/admin/license-adjustments/{request}/{verb}";

    private async Task<AdjustmentRequestDto> RequestAsync(Tenant t, int credits, string? token = null, string reason = "Goodwill credit approved by finance")
    {
        var response = await App.PostAsync(Adjust(t.LicenseId), new AdjustLicenseRequest(credits, reason), token ?? Platform.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString().ShouldContain("/api/v1/admin/license-adjustments/");
        return (await response.Content.ReadFromJsonAsync<AdjustmentRequestDto>(AuthApp.Json))!;
    }

    private Task<int> BalanceAsync(Tenant t) =>
        App.WithTenantDbAsync(t.ClientId, async db => (await db.Licenses.AsNoTracking().SingleAsync(l => l.Id == t.LicenseId)).Remaining);

    private Task<List<LicenseTransaction>> AdjustmentRowsAsync(Tenant t) =>
        App.WithTenantDbAsync(t.ClientId, db => db.LicenseTransactions.AsNoTracking().Where(x => x.Type == LedgerEntryType.Adjustment).ToListAsync());

    [Fact]
    public async Task An_adjustment_within_the_cap_is_applied_at_once()
    {
        var t = await NewTenantAsync("AP1", credits: 500);

        var response = await App.PostAsync(Adjust(t.LicenseId), new AdjustLicenseRequest(100, "Small correction"), Platform.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<LicenseDto>(AuthApp.Json))!.RemainingCredits.ShouldBe(600);
        (await AdjustmentRowsAsync(t)).ShouldHaveSingleItem().Credits.ShouldBe(100);
        (await App.WithDbAsync(db => db.LicenseAdjustmentRequests.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task A_large_adjustment_only_files_a_request_and_writes_nothing_to_the_ledger()
    {
        var t = await NewTenantAsync("AP2", credits: 500);

        var pending = await RequestAsync(t, 5000);

        pending.Status.ShouldBe("Pending");
        pending.Credits.ShouldBe(5000);
        pending.ClientId.ShouldBe(t.ClientId);
        (pending.ExpiresAt - pending.RequestedAt).TotalHours.ShouldBe(24, 0.01);
        (await BalanceAsync(t)).ShouldBe(500);
        (await AdjustmentRowsAsync(t)).ShouldBeEmpty();
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "license.adjustment_requested"))).ShouldBe(1);
        var list = await GetAsync<PagedResultOf<AdjustmentRequestDto>>("/api/v1/admin/license-adjustments?status=Pending", Platform.AccessToken);
        list.Items.ShouldHaveSingleItem().Id.ShouldBe(pending.Id);
        (await GetAsync<AdjustmentRequestDto>($"/api/v1/admin/license-adjustments/{pending.Id}", Platform.AccessToken)).Status.ShouldBe("Pending");
        // a large reduction needs approval too (the cap is on the size of the change, up or down)
        (await RequestAsync(t, -300, reason: "Chargeback")).Credits.ShouldBe(-300);
        (await App.PostAsync(Adjust(Guid.NewGuid()), new AdjustLicenseRequest(5000, "Ghost"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_requester_cannot_approve_their_own_request()
    {
        var t = await NewTenantAsync("AP3", credits: 500);
        var pending = await RequestAsync(t, 5000);

        var self = await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), Platform.AccessToken);

        self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await self.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe(ErrorCodes.SelfApprovalForbidden);
        (await GetAsync<AdjustmentRequestDto>($"/api/v1/admin/license-adjustments/{pending.Id}", Platform.AccessToken)).Status.ShouldBe("Pending");
        (await BalanceAsync(t)).ShouldBe(500);
        (await AdjustmentRowsAsync(t)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_different_approver_applies_it_through_the_ledger_and_it_cannot_be_applied_twice()
    {
        var t = await NewTenantAsync("AP4", credits: 500);
        var approver = await StaffAsync("approver@nexaverify.test", SystemRoles.SuperAdmin);
        var pending = await RequestAsync(t, 5000);

        var approved = await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest("Checked against the invoice"), approver.AccessToken);

        approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        var dto = (await approved.Content.ReadFromJsonAsync<AdjustmentRequestDto>(AuthApp.Json))!;
        dto.Status.ShouldBe("Approved");
        dto.DecidedBy.ShouldBe(approver.User.Id);
        dto.RequestedBy.ShouldNotBe(approver.User.Id);
        dto.LedgerTransactionId.ShouldNotBeNull();
        (await BalanceAsync(t)).ShouldBe(5500);
        var row = (await AdjustmentRowsAsync(t)).ShouldHaveSingleItem();
        (row.Id, row.Credits, row.ActorId).ShouldBe((dto.LedgerTransactionId!.Value, 5000, approver.User.Id));
        row.Reason!.ShouldContain("Goodwill credit approved by finance");
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{t.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeTrue();
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "license.adjustment_approved"))).ShouldBe(1);

        var again = await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), approver.AccessToken);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe(ErrorCodes.ApprovalNotPending);
        (await App.PostAsync(Decide(pending.Id, "reject"), new RejectAdjustmentRequest("too late"), approver.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BalanceAsync(t)).ShouldBe(5500);
        (await AdjustmentRowsAsync(t)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_approvals_apply_the_adjustment_exactly_once()
    {
        var t = await NewTenantAsync("AP5", credits: 500);
        var approverA = await StaffAsync("approver.a@nexaverify.test", SystemRoles.SuperAdmin);
        var approverB = await StaffAsync("approver.b@nexaverify.test", SystemRoles.SuperAdmin);
        var pending = await RequestAsync(t, 5000);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), i % 2 == 0 ? approverA.AccessToken : approverB.AccessToken)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(7);
        (await BalanceAsync(t)).ShouldBe(5500);
        (await AdjustmentRowsAsync(t)).ShouldHaveSingleItem();
        (await GetAsync<LedgerVerificationDto>($"/api/v1/admin/licenses/{t.LicenseId}/verify-ledger", Platform.AccessToken)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task A_rejected_request_never_reaches_the_ledger_and_needs_a_reason()
    {
        var t = await NewTenantAsync("AP6", credits: 500);
        var approver = await StaffAsync("approver.r@nexaverify.test", SystemRoles.SuperAdmin);
        var pending = await RequestAsync(t, 5000);

        (await App.PostAsync(Decide(pending.Id, "reject"), new RejectAdjustmentRequest(""), approver.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var rejected = await App.PostAsync(Decide(pending.Id, "reject"), new RejectAdjustmentRequest("No invoice on file"), approver.AccessToken);

        rejected.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = (await rejected.Content.ReadFromJsonAsync<AdjustmentRequestDto>(AuthApp.Json))!;
        (dto.Status, dto.DecisionNote, dto.DecidedBy).ShouldBe(("Rejected", "No invoice on file", approver.User.Id));
        (await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), approver.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BalanceAsync(t)).ShouldBe(500);
        (await AdjustmentRowsAsync(t)).ShouldBeEmpty();
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "license.adjustment_rejected"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_request_cannot_be_approved_after_its_window_and_the_expiry_is_recorded_once()
    {
        var t = await NewTenantAsync("AP7", credits: 500);
        var approver = await StaffAsync("approver.e@nexaverify.test", SystemRoles.SuperAdmin);
        var pending = await RequestAsync(t, 5000);
        await App.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE licensing.LicenseAdjustmentRequests SET ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME())");
            return true;
        });

        (await GetAsync<AdjustmentRequestDto>($"/api/v1/admin/license-adjustments/{pending.Id}", Platform.AccessToken)).Status.ShouldBe("Expired");
        (await GetAsync<PagedResultOf<AdjustmentRequestDto>>("/api/v1/admin/license-adjustments?status=Pending", Platform.AccessToken)).Items.ShouldBeEmpty();
        (await GetAsync<PagedResultOf<AdjustmentRequestDto>>("/api/v1/admin/license-adjustments?status=Expired", Platform.AccessToken)).Items.ShouldHaveSingleItem();
        for (var i = 0; i < 2; i++)
        {
            var late = await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), approver.AccessToken);
            late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await late.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe(ErrorCodes.ApprovalExpired);
        }

        (await App.PostAsync(Decide(pending.Id, "reject"), new RejectAdjustmentRequest("late"), approver.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BalanceAsync(t)).ShouldBe(500);
        (await AdjustmentRowsAsync(t)).ShouldBeEmpty();
        (await App.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Action == "license.adjustment_expired"))).ShouldBe(1);
        (await App.WithDbAsync(db => db.LicenseAdjustmentRequests.AsNoTracking().SingleAsync())).Status.ShouldBe(AdjustmentStatus.Expired);
    }

    [Fact]
    public async Task An_adjustment_that_cannot_be_applied_leaves_the_request_pending()
    {
        var t = await NewTenantAsync("AP8", credits: 500);
        var approver = await StaffAsync("approver.n@nexaverify.test", SystemRoles.SuperAdmin);
        var pending = await RequestAsync(t, -9000, reason: "Remove everything"); // far more than the balance

        var response = await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), approver.AccessToken);

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await GetAsync<AdjustmentRequestDto>($"/api/v1/admin/license-adjustments/{pending.Id}", Platform.AccessToken)).Status.ShouldBe("Pending");
        (await BalanceAsync(t)).ShouldBe(500);
        (await AdjustmentRowsAsync(t)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Requesting_needs_the_adjust_permission_and_deciding_needs_the_approve_permission()
    {
        var t = await NewTenantAsync("AP9", credits: 500);
        var requesterOnly = await StaffWithPermissionsAsync("requester@nexaverify.test", "AdjustOnly", Permissions.Licenses.Adjust, Permissions.Licenses.Read);
        var readerOnly = await StaffWithPermissionsAsync("reader@nexaverify.test", "ReadOnlyStaff", Permissions.Licenses.Read);

        var pending = await RequestAsync(t, 5000, requesterOnly.AccessToken);

        (await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), requesterOnly.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync(Decide(pending.Id, "reject"), new RejectAdjustmentRequest("x"), requesterOnly.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync(Adjust(t.LicenseId), new AdjustLicenseRequest(5000, "x"), readerOnly.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await App.GetAsync("/api/v1/admin/license-adjustments")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // client credentials never reach the platform queue
        (await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), t.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await App.GetAsync("/api/v1/admin/license-adjustments", t.Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // a person with the permission but who is not the requester CAN approve
        var approver = await StaffAsync("approver.p@nexaverify.test", SystemRoles.SuperAdmin);
        (await App.PostAsync(Decide(pending.Id, "approve"), new ApproveAdjustmentRequest(null), approver.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_requests_and_bad_input_are_rejected()
    {
        var t = await NewTenantAsync("AP10", credits: 500);

        (await App.PostAsync(Decide(Guid.NewGuid(), "approve"), new ApproveAdjustmentRequest(null), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.PostAsync(Decide(Guid.NewGuid(), "reject"), new RejectAdjustmentRequest("x"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync($"/api/v1/admin/license-adjustments/{Guid.NewGuid()}", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await App.GetAsync("/api/v1/admin/license-adjustments?status=Bogus", Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PostAsync(Adjust(t.LicenseId), new AdjustLicenseRequest(5000, ""), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PostAsync(Adjust(t.LicenseId), new AdjustLicenseRequest(0, "zero"), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await App.PostAsync(Decide(Guid.NewGuid(), "approve"), new ApproveAdjustmentRequest(new string('x', 600)), Platform.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private sealed record PagedResultOf<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
}
