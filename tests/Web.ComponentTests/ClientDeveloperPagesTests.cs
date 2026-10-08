using NexaVerify.Contracts.Api;
using NexaVerify.Contracts.Common;
using ApiKeysPage = NexaVerify.Web.Pages.Client.ApiKeys;
using ApiLogsPage = NexaVerify.Web.Pages.Client.ApiLogs;
using WebhookDetailPage = NexaVerify.Web.Pages.Client.WebhookDetail;
using WebhooksPage = NexaVerify.Web.Pages.Client.Webhooks;

namespace NexaVerify.Web.ComponentTests;

public class ApiKeysPageTests : ClientPageTestBase
{
    private (IRenderedComponent<ApiKeysPage> Page, IRenderedComponent<MudDialogProvider> Providers) OpenCreate()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<ApiKeysPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=create-key]").Count.ShouldBe(1));
        cut.Find("[data-testid=create-key]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=api-key-fields]").Count.ShouldBe(1));
        return (cut, providers);
    }

    [Fact]
    public void Keys_are_listed_with_status_prefix_and_usage_but_never_a_secret()
    {
        SignInAsClientAdmin();
        var cut = Render<ApiKeysPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        var row = cut.Find("table tbody tr");
        row.TextContent.ShouldContain("Front desk");
        row.TextContent.ShouldContain("nv_live_ab");
        row.TextContent.ShouldContain("1,234");
        row.QuerySelector(".nv-status-chip")!.GetAttribute("data-status").ShouldBe("Active");
        cut.Markup.ShouldNotContain(ClientSample.RawKey);
    }

    [Fact]
    public void Read_only_users_cannot_create_edit_replace_or_revoke()
    {
        SignInAs(WebPermissions.ApiKeysRead);
        var cut = Render<ApiKeysPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.FindAll("[data-testid=create-key],[data-testid=edit-key],[data-testid=regenerate-key],[data-testid=revoke-key]").ShouldBeEmpty();
    }

    [Fact]
    public void Empty_and_failed_lists_use_the_standard_states()
    {
        SignInAsClientAdmin();
        ApiKeys.Keys = () => Ok.Of<IReadOnlyList<ApiKeyDto>>([]);
        var empty = Render<ApiKeysPage>();
        empty.WaitForAssertion(() => empty.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));

        ApiKeys.Keys = () => Ok.Fail<IReadOnlyList<ApiKeyDto>>(correlation: "corr-keys");
        var failed = Render<ApiKeysPage>();
        failed.WaitForAssertion(() => failed.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-keys"));
    }

    [Fact]
    public void The_new_key_is_shown_once_and_is_gone_afterwards()
    {
        var (cut, providers) = OpenCreate();
        providers.Markup.ShouldNotContain("Not a face scope");
        Fill(providers, "Key name", "Kiosk");
        providers.Find(".mud-dialog input[type=checkbox]").Change(true);

        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Find("[data-testid=secret-value]").TextContent.ShouldBe(ClientSample.RawKey));
        ApiKeys.Calls.ShouldContain("create:Kiosk:faces.verify::");
        providers.Find(".mud-dialog input[type=checkbox]").Change(true);
        providers.Find("[data-testid=secret-done]").Click();

        providers.WaitForAssertion(() => providers.FindAll("[data-testid=secret-value]").ShouldBeEmpty());
        providers.Markup.ShouldNotContain(ClientSample.RawKey);
        cut.Markup.ShouldNotContain(ClientSample.RawKey);
    }

    [Fact]
    public void A_key_needs_a_name_and_at_least_one_permission_before_the_api_is_called()
    {
        var (_, providers) = OpenCreate();

        providers.Find("[data-testid=form-submit]").Click();
        providers.WaitForAssertion(() => providers.Markup.ShouldContain("Give the key a name"));

        Fill(providers, "Key name", "Kiosk");
        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Markup.ShouldContain("Choose at least one thing this key may do."));
        ApiKeys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Bad_ip_addresses_are_explained_inline()
    {
        var (_, providers) = OpenCreate();
        Fill(providers, "Key name", "Kiosk");
        providers.Find(".mud-dialog input[type=checkbox]").Change(true);
        Fill(providers, "Allowed IP addresses (optional)", "999.1.1.1");

        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Markup.ShouldContain("is not a valid IP address or range"));
        ApiKeys.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void A_replacement_key_is_also_shown_only_once()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<ApiKeysPage>();
        cut.WaitForAssertion(() => cut.FindAll("table [data-testid=regenerate-key]").Count.ShouldBe(1));

        cut.Find("[data-testid=regenerate-key]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=form-submit]").Count.ShouldBe(1));
        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Find("[data-testid=secret-value]").TextContent.ShouldBe(ClientSample.RawKey2));
        ApiKeys.Calls.ShouldContain("regenerate:60");
        providers.Find(".mud-dialog input[type=checkbox]").Change(true);
        providers.Find("[data-testid=secret-done]").Click();
        providers.WaitForAssertion(() => providers.Markup.ShouldNotContain(ClientSample.RawKey2));
    }

    [Fact]
    public void Revoking_needs_a_reason()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<ApiKeysPage>();
        cut.WaitForAssertion(() => cut.FindAll("table [data-testid=revoke-key]").Count.ShouldBe(1));

        cut.Find("[data-testid=revoke-key]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        providers.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue();
        providers.Find(".mud-dialog textarea").Input("leaked in a screenshot");
        providers.Find("[data-testid=confirm-ok]").Click();

        cut.WaitForAssertion(() => ApiKeys.Calls.ShouldContain("revoke:leaked in a screenshot"));
    }

    [Fact]
    public void Revoked_keys_offer_no_actions_except_viewing_their_calls()
    {
        SignInAsClientAdmin();
        ApiKeys.Keys = () => Ok.Of<IReadOnlyList<ApiKeyDto>>([ClientSample.Key() with { Status = "Revoked", EffectiveStatus = "Revoked" }]);

        var cut = Render<ApiKeysPage>();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.FindAll("[data-testid=edit-key],[data-testid=regenerate-key],[data-testid=revoke-key]").ShouldBeEmpty();
    }
}

public class WebhooksPageTests : ClientPageTestBase
{
    [Fact]
    public void Endpoints_are_listed_and_the_actions_follow_the_permission()
    {
        SignInAsClientAdmin();
        var cut = Render<WebhooksPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.Find("table tbody tr").TextContent.ShouldContain("Back office");
        cut.FindAll("table [data-testid=edit-webhook]").Count.ShouldBe(1);

        ApiKeys.Calls.Clear();
        Webhooks.Endpoints = () => Ok.Of<IReadOnlyList<WebhookEndpointDto>>([]);
        var empty = Render<WebhooksPage>();
        empty.WaitForAssertion(() => empty.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));
    }

    [Fact]
    public void A_signing_secret_is_shown_once_when_an_endpoint_is_added()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<WebhooksPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=create-webhook]").Count.ShouldBe(1));

        cut.Find("[data-testid=create-webhook]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=webhook-fields]").Count.ShouldBe(1));
        Fill(providers, "Name", "Back office");
        Fill(providers, "Address that receives events", "https://hooks.acme.test/nv");
        providers.Find(".mud-dialog input[type=checkbox]").Change(true);
        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Find("[data-testid=secret-value]").TextContent.ShouldBe(ClientSample.WebhookSecret));
        Webhooks.Calls.ShouldContain("create:Back office:https://hooks.acme.test/nv:recognition.completed");
        providers.Find(".mud-dialog input[type=checkbox]").Change(true);
        providers.Find("[data-testid=secret-done]").Click();
        providers.WaitForAssertion(() => providers.Markup.ShouldNotContain(ClientSample.WebhookSecret));
        cut.Markup.ShouldNotContain(ClientSample.WebhookSecret);
    }

    [Fact]
    public void Plain_http_addresses_and_missing_events_are_rejected_inline()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<WebhooksPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=create-webhook]").Count.ShouldBe(1));
        cut.Find("[data-testid=create-webhook]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=webhook-fields]").Count.ShouldBe(1));
        Fill(providers, "Name", "Back office");
        Fill(providers, "Address that receives events", "http://insecure.test/hook");

        providers.Find("[data-testid=form-submit]").Click();

        providers.WaitForAssertion(() => providers.Markup.ShouldContain("must start with https://"));
        providers.Markup.ShouldContain("Choose at least one event.");
        Webhooks.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void An_endpoint_can_be_switched_off_and_deleted_after_confirmation()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<WebhooksPage>();
        cut.WaitForAssertion(() => cut.FindAll("table [data-testid=toggle-webhook]").Count.ShouldBe(1));

        cut.Find("[data-testid=toggle-webhook]").Click();
        cut.WaitForAssertion(() => Webhooks.Calls.ShouldContain("update:False"));

        cut.Find("[data-testid=delete-webhook]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        Webhooks.Calls.ShouldNotContain("delete");
        providers.Find("[data-testid=confirm-ok]").Click();
        cut.WaitForAssertion(() => Webhooks.Calls.ShouldContain("delete"));
    }

    [Fact]
    public void The_delivery_log_offers_a_retry_only_for_abandoned_deliveries_and_keeps_remote_text_inert()
    {
        SignInAsClientAdmin();
        var cut = Render<WebhookDetailPage>(p => p.Add(x => x.Id, ClientSample.Hook().Id));

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
        cut.FindAll("table [data-testid=retry-delivery]").Count.ShouldBe(1);
        cut.Markup.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        cut.FindAll("script").ShouldBeEmpty();

        cut.Find("[data-testid=retry-delivery]").Click();
        cut.WaitForAssertion(() => Webhooks.Calls.ShouldContain("retry:12"));
    }

    [Fact]
    public void A_test_event_can_be_sent_and_a_new_secret_needs_confirmation_and_is_shown_once()
    {
        SignInAsClientAdmin();
        var providers = Providers();
        var cut = Render<WebhookDetailPage>(p => p.Add(x => x.Id, ClientSample.Hook().Id));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=send-test]").Count.ShouldBe(1));

        cut.Find("[data-testid=send-test]").Click();
        cut.WaitForAssertion(() => Webhooks.Calls.ShouldContain("test"));

        cut.Find("[data-testid=rotate-secret]").Click();
        providers.WaitForAssertion(() => providers.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        Webhooks.Calls.ShouldNotContain("rotate");
        providers.Find("[data-testid=confirm-ok]").Click();
        providers.WaitForAssertion(() => providers.Find("[data-testid=secret-value]").TextContent.ShouldBe(ClientSample.WebhookSecret));
        providers.Find(".mud-dialog input[type=checkbox]").Change(true);
        providers.Find("[data-testid=secret-done]").Click();
        providers.WaitForAssertion(() => providers.Markup.ShouldNotContain(ClientSample.WebhookSecret));
    }
}

public class ApiLogsPageTests : ClientPageTestBase
{
    [Fact]
    public async Task Logs_are_filtered_by_result_through_the_api_and_show_references()
    {
        SignInAsClientAdmin();
        var cut = Render<ApiLogsPage>();
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(1));
        cut.Find("table tbody tr").TextContent.ShouldContain("POST /api/v1/faces/verify");
        cut.Find("table tbody tr").TextContent.ShouldContain("corr-1");
        cut.Find("table tbody tr").TextContent.ShouldContain("Front desk");

        var status = cut.FindComponents<MudSelect<string>>().First();
        await cut.InvokeAsync(() => status.Instance.ValueChanged.InvokeAsync("ClientError"));
        cut.Find("[data-testid=apply-filters]").Click();

        cut.WaitForAssertion(() => ApiKeys.Calls.ShouldContain("logs:ClientError:"));
    }

    [Fact]
    public void The_usage_report_downloads_through_the_portal()
    {
        SignInAsClientAdmin();

        var cut = Render<ApiLogsPage>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=download-csv]").Count.ShouldBe(1));
        cut.Find("[data-testid=download-csv]").GetAttribute("href")!.ShouldStartWith("bff/client/reports/usage.csv?from=");
    }
}
