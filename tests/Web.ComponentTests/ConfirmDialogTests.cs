namespace NexaVerify.Web.ComponentTests;

public class ConfirmDialogTests : UiTestBase
{
    private async Task<(IRenderedComponent<MudDialogProvider> Provider, Task<ConfirmResult?> Result)> OpenAsync(string? phrase = null, bool reason = false)
    {
        var provider = RenderProviders();
        var dialogs = Services.GetRequiredService<IDialogService>();
        Task<ConfirmResult?>? task = null;
        await provider.InvokeAsync(() => { task = dialogs.ConfirmAsync("Suspend?", "This signs everyone out.", "Suspend", destructive: true, requiredPhrase: phrase, requireReason: reason); return Task.CompletedTask; });
        provider.WaitForAssertion(() => provider.FindAll("[data-testid=confirm-ok]").Count.ShouldBe(1));
        return (provider, task!);
    }

    [Fact]
    public async Task Simple_confirm_enables_button_and_returns_result()
    {
        var (provider, result) = await OpenAsync();

        provider.Find("[data-testid=confirm-message]").TextContent.ShouldBe("This signs everyone out.");
        provider.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeFalse();
        provider.Find("[data-testid=confirm-ok]").Click();

        (await result).ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancel_returns_null()
    {
        var (provider, result) = await OpenAsync();

        provider.Find("[data-testid=confirm-cancel]").Click();

        (await result).ShouldBeNull();
    }

    [Fact]
    public async Task Type_to_confirm_keeps_button_disabled_until_phrase_matches_exactly()
    {
        var (provider, result) = await OpenAsync(phrase: "Acme Corp");

        provider.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue();

        var input = provider.Find(".mud-dialog input");
        input.Input("acme corp");
        provider.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue("case must match");

        input.Input("Acme Corp");
        provider.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeFalse();
        provider.Find("[data-testid=confirm-ok]").Click();

        (await result).ShouldNotBeNull();
    }

    [Fact]
    public async Task Reason_is_required_and_returned()
    {
        var (provider, result) = await OpenAsync(reason: true);

        provider.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeTrue();
        provider.Find(".mud-dialog textarea").Input("  payment overdue ");
        provider.Find("[data-testid=confirm-ok]").HasAttribute("disabled").ShouldBeFalse();
        provider.Find("[data-testid=confirm-ok]").Click();

        (await result)!.Reason.ShouldBe("payment overdue");
    }

    [Fact]
    public async Task Message_is_rendered_as_text_not_markup()
    {
        var provider = RenderProviders();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => { _ = dialogs.ConfirmAsync("T", "<b id='x'>bold</b>"); return Task.CompletedTask; });
        provider.WaitForAssertion(() => provider.FindAll("[data-testid=confirm-message]").Count.ShouldBe(1));

        provider.FindAll("#x").ShouldBeEmpty();
    }
}
