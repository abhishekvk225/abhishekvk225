using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using NexaVerify.Contracts.Common;
using NexaVerify.Contracts.Licensing;
using NexaVerify.Contracts.Tenancy;
using NexaVerify.Web.Components.Admin;
using ClientDetailPage = NexaVerify.Web.Pages.Admin.ClientDetail;
using CostRulesPage = NexaVerify.Web.Pages.Admin.CostRules;
using CreateClientPage = NexaVerify.Web.Pages.Admin.CreateClient;
using LicenseDetailPage = NexaVerify.Web.Pages.Admin.LicenseDetail;
using LicensesPage = NexaVerify.Web.Pages.Admin.Licenses;
using PlansPage = NexaVerify.Web.Pages.Admin.Plans;
using PlatformUsersPage = NexaVerify.Web.Pages.Admin.PlatformUsers;
using ReportsPage = NexaVerify.Web.Pages.Admin.Reports;
using RolesPage = NexaVerify.Web.Pages.Admin.Roles;

namespace NexaVerify.Web.ComponentTests;

/// <summary>Opening a dialog, filling it in, submitting it: one test per mutation path of the admin screens.</summary>
public class AdminMutationTests : PageTestBase
{
    private IRenderedComponent<MudDialogProvider> _providers = null!;

    private void Arrange()
    {
        SignInAsEverything();
        _providers = Providers();
    }

    private static void Type(IRenderedComponent<IComponent> host, string label, string value) =>
        host.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == label).Find("input,textarea").Input(value);

    private static void Check(IRenderedComponent<IComponent> host, string label, bool on = true) =>
        host.FindComponents<MudCheckBox<bool>>().Single(c => c.Instance.Label == label).Find("input").Change(on);

    private void Submit() => _providers.Find("[data-testid=form-submit]").Click();

    // ---- roles ----

    [Fact]
    public void A_new_role_is_created_with_the_ticked_permissions()
    {
        Arrange();
        var cut = Render<RolesPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=new-role]").Count.ShouldBe(1));

        cut.Find("[data-testid=new-role]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Submit();
        _providers.Markup.ShouldContain("Enter a role name.");
        Access.Calls.ShouldBeEmpty();

        Type(_providers, "Role name", "Auditor");
        Check(_providers, "View clients");
        Check(_providers, "View the admin dashboard");
        Submit();

        cut.WaitForAssertion(() => Access.Calls.ShouldContain("role-create:Auditor:Platform:clients.read+dashboard.admin"));
    }

    [Fact]
    public void An_existing_role_keeps_its_permissions_and_saves_the_changes()
    {
        Arrange();
        var snacks = Render<MudSnackbarProvider>();
        var cut = Render<RolesPage>();
        cut.WaitForAssertion(() => cut.FindAll("[aria-label='Edit role Support']").Count.ShouldBe(1));

        cut.Find("[aria-label='Edit role Support']").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Check(_providers, "Create clients");
        Submit();

        cut.WaitForAssertion(() => Access.Calls.ShouldContain("role-update:clients.create+clients.read"));
        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("within a few minutes"));
        snacks.Markup.ShouldNotContain("without anyone signing in again");
    }

    [Fact]
    public void The_roles_page_does_not_promise_instant_effect()
    {
        Arrange();

        var cut = Render<RolesPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=permission-matrix]").Count.ShouldBe(1));
        cut.Markup.ShouldNotContain("without anyone signing in again");
    }

    // ---- plans ----

    [Fact]
    public void A_new_plan_needs_its_basics_then_is_created()
    {
        Arrange();
        var cut = Render<PlansPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=new-plan]").Count.ShouldBe(1));

        cut.Find("[data-testid=new-plan]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Submit();
        _providers.Markup.ShouldContain("Enter a short code.");
        Licensing.Calls.ShouldBeEmpty();

        Type(_providers, "Code", "SILVER-1");
        Type(_providers, "Plan name", "Silver");
        Submit();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("plan-create:SILVER-1"));
    }

    [Fact]
    public void An_existing_plan_can_be_renamed_but_keeps_its_code()
    {
        Arrange();
        var cut = Render<PlansPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr button").Count.ShouldBe(1));

        cut.Find("table tbody tr button").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        _providers.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "Code").Instance.Disabled.ShouldBeTrue();
        Type(_providers, "Plan name", "Gold");
        Submit();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("plan-update:Gold"));
    }

    // ---- cost rules ----

    [Fact]
    public void Cost_rules_list_and_the_default_price_can_be_changed()
    {
        Arrange();
        var cut = Render<CostRulesPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.Find("table tbody tr").TextContent.ShouldContain("Everyone (default)");

        cut.Find("[data-testid=set-default-rule]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        _providers.FindComponent<MudNumericField<int>>().Find("input").Change("3");
        Submit();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("rule-default:Verify:3:OnCompleted"));
    }

    [Fact]
    public async Task A_plan_price_needs_a_plan()
    {
        Arrange();
        var cut = Render<CostRulesPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=set-plan-rule]").Count.ShouldBe(1));
        cut.Find("[data-testid=set-plan-rule]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));

        Submit();
        _providers.WaitForAssertion(() => _providers.Markup.ShouldContain("Choose the plan this price is for."));
        Licensing.Calls.ShouldNotContain(c => c.StartsWith("rule-plan", StringComparison.Ordinal));

        var select = _providers.FindComponents<MudSelect<Guid?>>().First();
        var planId = (await Licensing.ListPlansAsync()).Value[0].Id;
        await _providers.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(planId));
        Submit();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain($"rule-plan:{planId}:Verify:1"));
    }

    [Fact]
    public void A_failed_plan_lookup_is_reported_on_the_cost_rules_page()
    {
        Arrange();
        var snacks = Render<MudSnackbarProvider>();
        Licensing.Plans = () => Task.FromResult(ApiResult<IReadOnlyList<PlanDto>>.Fail("INTERNAL_ERROR", "Something went wrong on our side. Please try again.", "corr-pl", 500));

        var cut = Render<CostRulesPage>();

        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("corr-pl"));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
    }

    // ---- platform users ----

    [Fact]
    public void A_platform_user_needs_a_strong_enough_temporary_password_and_a_role()
    {
        Arrange();
        var cut = Render<PlatformUsersPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=new-platform-user]").Count.ShouldBe(1));

        cut.Find("[data-testid=new-platform-user]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Type(_providers, "Email address", "new@nexaverify.test");
        Type(_providers, "Full name", "Nia New");
        Type(_providers, "Temporary password", "short");
        Submit();
        _providers.Markup.ShouldContain("Use at least 12 characters.");
        _providers.Markup.ShouldContain("Choose at least one role.");
        Access.Calls.ShouldBeEmpty();

        Type(_providers, "Temporary password", "Long-Enough-Password-1");
        Check(_providers, "Support");
        Submit();

        cut.WaitForAssertion(() => Access.Calls.ShouldContain("user-create:new@nexaverify.test:Support"));
    }

    [Fact]
    public void An_existing_platform_user_can_be_edited_and_deactivated()
    {
        Arrange();
        var cut = Render<PlatformUsersPage>();
        cut.WaitForAssertion(() => cut.FindAll("[aria-label='Edit Ada Admin']").Count.ShouldBeGreaterThan(0));

        cut.FindAll("[aria-label='Edit Ada Admin']").First().Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Type(_providers, "Full name", "Ada Lovelace");
        _providers.FindComponent<MudSwitch<bool>>().Find("input").Change(false);
        Submit();

        cut.WaitForAssertion(() => Access.Calls.ShouldContain("user-update:Ada Lovelace:SuperAdmin:False"));
    }

    [Fact]
    public void Without_permission_to_list_roles_the_add_user_dialog_explains_why_instead_of_opening()
    {
        Arrange();
        var snacks = Render<MudSnackbarProvider>();
        Access.Roles = () => Task.FromResult(ApiResult<IReadOnlyList<NexaVerify.Contracts.Identity.RoleDto>>.Fail("FORBIDDEN", "You don't have permission to do this.", "corr-roles", 403));
        var cut = Render<PlatformUsersPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=new-platform-user]").Count.ShouldBe(1));

        cut.Find("[data-testid=new-platform-user]").Click();

        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("corr-roles"));
        _providers.FindAll("[data-testid=form-submit]").ShouldBeEmpty();
    }

    // ---- license actions ----

    private IRenderedComponent<LicenseDetailPage> LicenseDetail()
    {
        var cut = Render<LicenseDetailPage>(p => p.Add(x => x.Id, Guid.NewGuid()));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=edit-license]").Count.ShouldBe(1));
        return cut;
    }

    [Fact]
    public void A_license_can_be_renamed_and_the_page_shows_the_new_name_without_reloading()
    {
        Arrange();
        var cut = LicenseDetail();
        cut.Find("[data-testid=edit-license]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));

        Type(_providers, "License name", "Annual 2027");
        Submit();

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Annual 2027"));
        Licensing.Calls.ShouldContain("update:Annual 2027");
    }

    [Fact]
    public void A_license_can_be_renewed_with_extra_credits()
    {
        Arrange();
        var cut = LicenseDetail();
        cut.Find("[data-testid=renew-license]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));

        _providers.FindComponent<MudNumericField<int>>().Find("input").Change("250");
        Submit();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain(c => c.StartsWith("renew:250:", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_charge_can_be_refunded_with_a_reason_without_the_page_flashing_a_skeleton()
    {
        Arrange();
        var cut = LicenseDetail();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr button").Count.ShouldBe(1));

        cut.Find("table tbody tr button").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        Submit();
        _providers.Markup.ShouldContain("Explain why this is refunded");
        Type(_providers, "Reason (recorded in the audit log)", "duplicate request");
        Submit();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain(c => c.EndsWith(":duplicate request", StringComparison.Ordinal) && c.StartsWith("refund:7:", StringComparison.Ordinal)));
        cut.FindAll("[data-testid=skeleton-card]").ShouldBeEmpty();
        cut.Find("h1").TextContent.ShouldBe("Annual 2026");
    }

    // ---- M5: plan failure on issue ----

    [Fact]
    public void Issuing_a_license_reports_a_failed_plan_lookup_instead_of_opening_an_unusable_form()
    {
        Arrange();
        var snacks = Render<MudSnackbarProvider>();
        Licensing.Plans = () => Task.FromResult(ApiResult<IReadOnlyList<PlanDto>>.Fail("INTERNAL_ERROR", "Something went wrong on our side. Please try again.", "corr-plans", 500));
        var cut = Render<LicensesPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=issue-license]").Count.ShouldBe(1));

        cut.Find("[data-testid=issue-license]").Click();

        snacks.WaitForAssertion(() => snacks.Markup.ShouldContain("corr-plans"));
        _providers.FindAll("[data-testid=form-submit]").ShouldBeEmpty();
    }

    [Fact]
    public void Issuing_a_license_from_a_client_page_needs_no_client_picker_and_creates_it()
    {
        Arrange();
        var clientId = Guid.NewGuid();
        var cut = Render<ClientLicensesTab>(p => p.Add(x => x.ClientId, clientId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=issue-license]").Count.ShouldBe(1));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));

        cut.Find("[data-testid=issue-license]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        _providers.FindAll("[data-testid=client-picker]").ShouldBeEmpty();
        Type(_providers, "License name", "Pilot");
        Submit();

        cut.WaitForAssertion(() => Licensing.Calls.ShouldContain("create:Pilot"));
    }

    [Fact]
    public void The_issue_dialog_on_the_license_list_asks_for_the_client_and_refuses_to_continue_without_one()
    {
        Arrange();
        var cut = Render<LicensesPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=issue-license]").Count.ShouldBe(1));

        cut.Find("[data-testid=issue-license]").Click();
        _providers.WaitForAssertion(() => _providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        _providers.FindAll("[data-testid=client-picker]").Count.ShouldBe(1);
        Type(_providers, "License name", "Pilot");
        Submit();

        _providers.WaitForAssertion(() => _providers.Markup.ShouldContain("Choose the client this license is for."));
        Licensing.Calls.ShouldNotContain(c => c.StartsWith("create", StringComparison.Ordinal));
    }

    // ---- client settings ----

    private static SettingDto Setting(string key, string group, string type, object value, bool editable = true, decimal? min = null, decimal? max = null) =>
        new(key, group, type, JsonSerializer.SerializeToElement(value), JsonSerializer.SerializeToElement(value), min, max, "Platform", editable, false, $"About {key}");

    private IRenderedComponent<ClientSettingsTab> RenderSettings()
    {
        Clients.Settings =
        [
            Setting("limits.maxUsers", "Limits", "Int", 10, min: 1, max: 1000),
            Setting("face.matchThreshold", "Recognition", "Decimal", 0.6m, min: 0.3m, max: 0.99m),
            Setting("notify.emailEnabled", "Notifications", "Bool", true),
            Setting("integration.allowedIps", "Integration", "StringList", new[] { "10.0.0.1" }),
            Setting("webhook.enabled", "Integration", "Bool", true, editable: false),
        ];
        return Render<ClientSettingsTab>(p => p.Add(x => x.ClientId, Guid.NewGuid()));
    }

    [Fact]
    public async Task Only_changed_settings_are_sent_and_each_keeps_its_type()
    {
        Arrange();
        var cut = RenderSettings();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid^=setting-]").Count.ShouldBe(5));

        await cut.InvokeAsync(() => cut.FindComponents<MudNumericField<decimal>>().First().Instance.ValueChanged.InvokeAsync(25m));
        await cut.InvokeAsync(() => cut.FindComponent<MudSwitch<bool>>().Instance.ValueChanged.InvokeAsync(false));
        cut.FindComponents<MudTextField<string>>().Single().Find("textarea").Input("10.0.0.1\n10.0.0.2\n");
        cut.Find("[data-testid=settings-save]").Click();

        cut.WaitForAssertion(() => Clients.LastSettingsRequest.ShouldNotBeNull());
        var values = Clients.LastSettingsRequest!.Values;
        values.Keys.OrderBy(k => k).ShouldBe(["integration.allowedIps", "limits.maxUsers", "notify.emailEnabled"]);
        values["limits.maxUsers"].ValueKind.ShouldBe(JsonValueKind.Number);
        values["limits.maxUsers"].GetRawText().ShouldBe("25", "whole numbers must not travel as 25.0");
        values["notify.emailEnabled"].ValueKind.ShouldBe(JsonValueKind.False);
        values["integration.allowedIps"].EnumerateArray().Select(e => e.GetString()).ShouldBe(["10.0.0.1", "10.0.0.2"]);
        values.ContainsKey("webhook.enabled").ShouldBeFalse("read-only settings are never sent");
    }

    [Fact]
    public void Saving_without_changes_sends_nothing()
    {
        Arrange();
        var cut = RenderSettings();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid^=setting-]").Count.ShouldBe(5));

        cut.Find("[data-testid=settings-save]").Click();

        Clients.LastSettingsRequest.ShouldBeNull();
    }

    [Fact]
    public void Read_only_settings_are_disabled()
    {
        Arrange();
        var cut = RenderSettings();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid^=setting-]").Count.ShouldBe(5));

        cut.Find("[data-testid=setting-webhook\\.enabled] input").HasAttribute("disabled").ShouldBeTrue();
    }

    // ---- audit panel ----

    [Fact]
    public void The_audit_panel_has_activity_and_sign_in_views()
    {
        Arrange();

        var cut = Render<ClientAuditPanel>(p => p.Add(x => x.ClientId, Guid.NewGuid()));

        cut.WaitForAssertion(() => cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).ShouldBe(["Activity", "Sign-ins"]));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No activity recorded for this client yet."));
    }

    // ---- wizard double submit ----

    [Fact]
    public void Pressing_enter_twice_on_the_last_wizard_step_creates_the_client_once()
    {
        Arrange();
        var gate = new Gate<ApiResult<ClientDto>>();
        Clients.Create = _ => gate.Task;
        var cut = Render<CreateClientPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=wizard-next]").Count.ShouldBe(1));
        Type(cut, "Client code", "TWICE");
        Type(cut, "Company name", "Twice Co");
        Type(cut, "Contact email", "a@twice.test");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Administrator's email"));
        Type(cut, "Administrator's full name", "Tia");
        Type(cut, "Administrator's email", "tia@twice.test");
        cut.Find("form").Submit();
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => cut.Find("[data-testid=wizard-next]").TextContent.ShouldContain("Create client"));

        cut.Find("form").Submit();
        cut.Find("form").Submit();
        cut.Find("form").Submit();

        Clients.Calls.Count(c => c == "create:TWICE").ShouldBe(1);
        gate.Release(ApiResult<ClientDto>.Ok(Sample.Client(Guid.NewGuid())));
    }

    // ---- stale responses ----

    [Fact]
    public void A_slow_answer_for_the_previous_client_never_replaces_the_current_one()
    {
        Arrange();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var slowA = new Gate<ApiResult<ClientDto>>();
        Clients.Get = id => id == a ? slowA.Task : Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(b) with { Name = "Client B" }));
        var cut = Render<ClientDetailPage>(p => p.Add(x => x.Id, a));

        cut.Render(p => p.Add(x => x.Id, b));
        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Client B"));
        slowA.Release(ApiResult<ClientDto>.Ok(Sample.Client(a) with { Name = "Client A" }));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Client B"));
        cut.Markup.ShouldNotContain("Client A");
    }

    [Fact]
    public void Re_rendering_with_the_same_id_does_not_reload_the_client()
    {
        Arrange();
        var id = Guid.NewGuid();
        var loads = 0;
        Clients.Get = _ =>
        {
            loads++;
            return Task.FromResult(ApiResult<ClientDto>.Ok(Sample.Client(id)));
        };
        var cut = Render<ClientDetailPage>(p => p.Add(x => x.Id, id));
        cut.WaitForAssertion(() => cut.FindAll("h1").Count.ShouldBe(1));

        cut.Render(p => p.Add(x => x.Id, id));
        cut.Render(p => p.Add(x => x.Id, id));

        loads.ShouldBe(1);
    }

    [Fact]
    public void Re_rendering_a_license_with_the_same_id_does_not_reload_it()
    {
        Arrange();
        var id = Guid.NewGuid();
        var cut = Render<LicenseDetailPage>(p => p.Add(x => x.Id, id));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=edit-license]").Count.ShouldBe(1));
        var before = Licensing.Calls.Count;

        cut.Render(p => p.Add(x => x.Id, id));

        cut.FindAll("[data-testid=skeleton-card]").ShouldBeEmpty();
        Licensing.Calls.Count.ShouldBe(before);
    }

    // ---- reports: long-running check ----

    [Fact]
    public void A_ledger_check_that_times_out_holds_the_button_back_and_explains_it_may_still_be_running()
    {
        Arrange();
        var snacks = Render<MudSnackbarProvider>();
        Licensing.VerifyAll = () => Task.FromResult(ApiResult<LedgerVerificationReportDto>.Fail(ApiGateway.TimeoutCode, "This is taking longer than expected.", null, 504));
        var cut = Render<ReportsPage>();

        cut.Find("[data-testid=verify-ledgers]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=verify-running]").TextContent.ShouldContain("may still be running"));
        cut.Find("[data-testid=verify-ledgers]").HasAttribute("disabled").ShouldBeTrue("no overlapping second run");
        snacks.Markup.ShouldContain("longer than expected");

        Clock.Advance(TimeSpan.FromMinutes(4));
        cut.Render();

        cut.Find("[data-testid=verify-ledgers]").HasAttribute("disabled").ShouldBeFalse();
        cut.FindAll("[data-testid=verify-running]").ShouldBeEmpty();
    }
}

public class FormDialogTests : UiTestBase
{
    private sealed class Model
    {
        [Required(ErrorMessage = "Name is required.")]
        public string Name { get; set; } = string.Empty;

        public string? Email { get; set; }
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Providers, Task<Model?> Result)> OpenAsync(Func<Model, Task<ApiError?>> submit)
    {
        var providers = RenderProviders();
        var dialogs = Services.GetRequiredService<IDialogService>();
        Task<Model?> result = Task.FromResult<Model?>(null);
        await providers.InvokeAsync(() =>
        {
            result = dialogs.ShowFormAsync("Edit", new Model(), m => builder =>
            {
                builder.OpenComponent<MudTextField<string>>(0);
                builder.AddAttribute(1, "Label", "Name");
                builder.AddAttribute(2, "Value", m.Name);
                builder.AddAttribute(3, "ValueChanged", EventCallback.Factory.Create<string>(this, v => m.Name = v));
                builder.AddAttribute(4, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => m.Name));
                builder.AddAttribute(5, "Immediate", true);
                builder.CloseComponent();
                builder.OpenComponent<MudTextField<string>>(6);
                builder.AddAttribute(7, "Label", "Email");
                builder.AddAttribute(8, "Value", m.Email);
                builder.AddAttribute(9, "ValueChanged", EventCallback.Factory.Create<string>(this, v => m.Email = v));
                builder.AddAttribute(10, "For", (System.Linq.Expressions.Expression<Func<string?>>)(() => m.Email));
                builder.CloseComponent();
            }, submit, "Save");
            return Task.CompletedTask;
        });
        return (providers, result);
    }

    [Fact]
    public async Task Field_errors_from_the_api_appear_under_the_matching_field_and_the_rest_in_a_summary_with_the_reference()
    {
        var (providers, _) = await OpenAsync(_ => Task.FromResult<ApiError?>(new ApiError("VALIDATION_FAILED", "Please fix the highlighted fields.", "corr-fd", 400,
            new Dictionary<string, string[]> { ["name"] = ["A client with this name already exists."], ["unknownThing"] = ["Something unmapped."] })));
        providers.Find("input").Input("Taken");

        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Markup.ShouldContain("A client with this name already exists."));
        var summary = providers.Find("[data-testid=form-error]").TextContent;
        summary.ShouldContain("Something unmapped.");
        summary.ShouldContain("corr-fd");
        providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1, "the dialog stays open");
    }

    [Fact]
    public async Task Client_side_validation_stops_the_submit()
    {
        var calls = 0;
        var (providers, _) = await OpenAsync(_ =>
        {
            calls++;
            return Task.FromResult<ApiError?>(null);
        });

        providers.Find("[data-testid=form-submit]").Click();

        providers.Markup.ShouldContain("Name is required.");
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_successful_submit_closes_the_dialog_and_returns_the_model()
    {
        var (providers, result) = await OpenAsync(_ => Task.FromResult<ApiError?>(null));
        providers.Find("input").Input("Fine");

        providers.Find("[data-testid=form-submit]").Click();

        (await result)!.Name.ShouldBe("Fine");
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=form-submit]").ShouldBeEmpty());
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_generic_message_never_raw_text()
    {
        var (providers, _) = await OpenAsync(_ => throw new InvalidOperationException("SQL connection string leaked: Server=db;Password=hunter2"));
        providers.Find("input").Input("Fine");

        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Find("[data-testid=form-error]").TextContent.ShouldContain("Something went wrong"));
        providers.Markup.ShouldNotContain("hunter2");
    }
}

public class ProblemMessageTests
{
    [Theory]
    [InlineData(500, "INTERNAL_ERROR", "System.NullReferenceException at X", "Something went wrong on our side. Please try again.")]
    [InlineData(502, "INTERNAL_ERROR", "upstream nginx details", "Something went wrong on our side. Please try again.")]
    [InlineData(503, "FACE_PROVIDER_UNAVAILABLE", "provider host 10.0.0.4 down", "The service is temporarily unavailable. Please try again shortly.")]
    [InlineData(401, "UNAUTHENTICATED", "token signature invalid", "Your session has ended. Please sign in again.")]
    [InlineData(401, "TOKEN_EXPIRED", "expired at", "Your session has ended. Please sign in again.")]
    [InlineData(403, "FORBIDDEN", "role X lacks Y", "You don't have permission to do this.")]
    [InlineData(404, "NOT_FOUND", "row 123", "We couldn't find that. It may have been removed.")]
    [InlineData(429, "RATE_LIMITED", "bucket 5", "Too many requests right now. Please wait a moment and try again.")]
    [InlineData(429, "DAILY_QUOTA_EXCEEDED", "x", "Too many requests right now. Please wait a moment and try again.")]
    [InlineData(409, "CONCURRENCY_CONFLICT", "rowversion", "Someone else changed this at the same time. Reload and try again.")]
    [InlineData(409, "LICENSE_INVALID_TRANSITION", "A revoked license cannot be activated.", "A revoked license cannot be activated.")]
    [InlineData(400, "VALIDATION_FAILED", "Name is required.", "Name is required.")]
    [InlineData(402, "LICENSE_EXPIRED", "The license ended on 1 May.", "The license ended on 1 May.")]
    [InlineData(422, "NO_FACE_DETECTED", "No face found in the image.", "No face found in the image.")]
    [InlineData(400, "VALIDATION_FAILED", null, "Some of the details are not valid. Please check them and try again.")]
    [InlineData(402, "LICENSE_EXPIRED", "  ", "The license does not allow this right now.")]
    [InlineData(409, "CONFLICT", null, "That conflicts with the current state. Reload and try again.")]
    [InlineData(418, "TEAPOT", "short and stout", "The request could not be completed. Please try again.")]
    public void Messages_are_plain_and_server_detail_is_only_shown_for_short_business_rules(int status, string code, string? detail, string expected) =>
        ProblemMapper.MessageFor(status, code, detail).ShouldBe(expected);

    [Fact]
    public void Long_business_messages_are_cut()
    {
        var message = ProblemMapper.MessageFor(400, "VALIDATION_FAILED", new string('x', 1000));

        message.Length.ShouldBeLessThan(400);
        message.ShouldEndWith("…");
    }
}

public class DataTableBehaviourTests : UiTestBase
{
    private sealed record Row(int Id);

    private static readonly IReadOnlyList<DataColumn<Row>> Columns = [DataColumn<Row>.Text("Id", r => r.Id.ToString())];

    [Fact]
    public async Task A_superseded_request_is_cancelled_and_so_is_the_one_in_flight_when_the_table_goes_away()
    {
        Render<MudPopoverProvider>();
        var tokens = new List<CancellationToken>();
        var cut = Render<DataTable<Row>>(p => p
            .Add(x => x.Columns, Columns)
            .Add(x => x.PageSize, 10)
            .Add(x => x.LoadPage, (PageRequest page, CancellationToken ct) =>
            {
                tokens.Add(ct);
                return tokens.Count == 1
                    ? Task.FromResult(ApiResult<PagedResult<Row>>.Ok(new PagedResult<Row>(Enumerable.Range(1, 10).Select(i => new Row(i)).ToList(), 1, 10, 30)))
                    : Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => ApiResult<PagedResult<Row>>.Ok(new PagedResult<Row>([], 1, 10, 0)), CancellationToken.None);
            }));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(10));

        var before = tokens.Count;
        cut.Find("[data-testid=next-page]").Click();
        cut.WaitForAssertion(() => tokens.Count.ShouldBe(before + 1));
        var superseded = tokens[^1];
        superseded.IsCancellationRequested.ShouldBeFalse();

        _ = cut.InvokeAsync(() => cut.Instance.ReloadAsync()); // never completes by itself: the request we will see cancelled on dispose
        cut.WaitForAssertion(() => tokens.Count.ShouldBe(before + 2));
        superseded.IsCancellationRequested.ShouldBeTrue("a newer request replaced it");
        var current = tokens[^1];
        current.IsCancellationRequested.ShouldBeFalse();

        cut.Instance.Dispose();
        current.IsCancellationRequested.ShouldBeTrue("the table was disposed");
    }

    [Fact]
    public void A_page_past_the_end_steps_back_to_the_last_real_page()
    {
        Render<MudPopoverProvider>();
        var requested = new List<int>();
        var cut = Render<DataTable<Row>>(p => p
            .Add(x => x.Columns, Columns)
            .Add(x => x.PageSize, 10)
            .Add(x => x.LoadPage, (PageRequest page, CancellationToken ct) =>
            {
                requested.Add(page.Page);
                // 30 rows at first; after the user is on page 3 the data shrinks to 12 rows (2 pages)
                var total = requested.Count >= 3 ? 12 : 30;
                var from = (page.Page - 1) * 10;
                var rows = Enumerable.Range(from + 1, Math.Max(0, Math.Min(10, total - from))).Select(i => new Row(i)).ToList();
                return Task.FromResult(ApiResult<PagedResult<Row>>.Ok(new PagedResult<Row>(rows, page.Page, 10, total)));
            }));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(10));
        cut.Find("[data-testid=next-page]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=range-text]").TextContent.ShouldContain("11"));

        cut.Find("[data-testid=next-page]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=range-text]").TextContent.ShouldContain("Showing 11–12 of 12"));
        requested.ShouldBe([1, 2, 3, 2]);
        cut.FindAll("[data-testid=empty-state]").ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_first_page_is_still_the_empty_state_and_does_not_loop()
    {
        Render<MudPopoverProvider>();
        var calls = 0;
        var cut = Render<DataTable<Row>>(p => p
            .Add(x => x.Columns, Columns)
            .Add(x => x.LoadPage, (PageRequest page, CancellationToken ct) =>
            {
                calls++;
                return Task.FromResult(ApiResult<PagedResult<Row>>.Ok(new PagedResult<Row>([], 1, 25, 0)));
            }));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));
        calls.ShouldBe(1);
    }
}
