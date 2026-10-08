namespace NexaVerify.Web.ComponentTests;

public class SecretRevealDialogTests : UiTestBase
{
    private const string Secret = "nv_live_SECRET_VALUE_123";

    [Fact]
    public async Task Shows_secret_until_confirmed_then_it_is_gone_from_the_dom()
    {
        var provider = RenderProviders();
        var dialogs = Services.GetRequiredService<IDialogService>();
        Task? shown = null;
        await provider.InvokeAsync(() => { shown = dialogs.RevealSecretAsync("New key", Secret); return Task.CompletedTask; });
        provider.WaitForAssertion(() => provider.FindAll("[data-testid=secret-value]").Count.ShouldBe(1));

        provider.Find("[data-testid=secret-value]").TextContent.ShouldBe(Secret);
        provider.Find("[data-testid=secret-done]").HasAttribute("disabled").ShouldBeTrue("must confirm 'I stored it' first");

        provider.Find(".mud-dialog input[type=checkbox]").Change(true);
        provider.Find("[data-testid=secret-done]").HasAttribute("disabled").ShouldBeFalse();
        provider.Find("[data-testid=secret-done]").Click();

        await shown!;
        provider.Markup.ShouldNotContain(Secret);
        provider.FindAll("[data-testid=secret-value]").ShouldBeEmpty();
    }

    [Fact]
    public async Task Copy_button_puts_the_secret_on_the_clipboard()
    {
        var provider = RenderProviders();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => { _ = dialogs.RevealSecretAsync("New key", Secret); return Task.CompletedTask; });
        provider.WaitForAssertion(() => provider.FindAll("[data-testid=secret-copy]").Count.ShouldBe(1));

        provider.Find("[data-testid=secret-copy]").Click();

        Services.GetRequiredService<FakeClipboard>().Last.ShouldBe(Secret);
    }

    [Fact]
    public async Task Closing_drops_the_parameter_and_the_component_holds_no_copy_of_the_secret()
    {
        var provider = RenderProviders();
        var dialogs = Services.GetRequiredService<IDialogService>();
        Task? shown = null;
        await provider.InvokeAsync(() => { shown = dialogs.RevealSecretAsync("New key", Secret); return Task.CompletedTask; });
        provider.WaitForAssertion(() => provider.FindAll("[data-testid=secret-value]").Count.ShouldBe(1));
        var dialog = provider.FindComponent<NexaVerify.Web.Components.SecretRevealDialog>().Instance;
        dialog.Secret.ShouldBe(Secret);

        provider.Find(".mud-dialog input[type=checkbox]").Change(true);
        provider.Find("[data-testid=secret-done]").Click();
        await shown!;

        dialog.Secret.ShouldBeNull();
        var fields = typeof(NexaVerify.Web.Components.SecretRevealDialog).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(string)).Select(f => f.GetValue(dialog) as string);
        fields.ShouldAllBe(v => v == null || !v.Contains(Secret));
    }
}
