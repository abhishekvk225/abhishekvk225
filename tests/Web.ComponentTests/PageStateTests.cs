namespace NexaVerify.Web.ComponentTests;

public class PageStateTests : UiTestBase
{
    private IRenderedComponent<PageState<string>> Render(ApiResult<string>? result, bool trackRetry = false, Action? onRetry = null) =>
        Render<PageState<string>>(p =>
        {
            p.Add(x => x.Result, result);
            p.Add(x => x.IsEmpty, s => s.Length == 0);
            p.Add(x => x.ChildContent, (string s) => $"<p id='ok'>value:{s}</p>");
            if (onRetry is not null)
            {
                p.Add(x => x.OnRetry, onRetry);
            }
        });

    [Fact]
    public void Null_result_is_loading()
    {
        var cut = Render(null);

        cut.FindAll("[data-testid=skeleton-card]").Count.ShouldBe(1);
        cut.FindAll("#ok").ShouldBeEmpty();
    }

    [Fact]
    public void Success_renders_content()
    {
        var cut = Render(ApiResult<string>.Ok("hello"));

        cut.Find("#ok").TextContent.ShouldBe("value:hello");
    }

    [Fact]
    public void Empty_success_renders_empty_state()
    {
        var cut = Render(ApiResult<string>.Ok(string.Empty));

        cut.FindAll("[data-testid=empty-state]").Count.ShouldBe(1);
        cut.FindAll("#ok").ShouldBeEmpty();
    }

    [Fact]
    public void Failure_renders_error_state_with_reference_and_retry()
    {
        var retried = false;
        var cut = Render(ApiResult<string>.Fail("X", "Nope", "corr-9"), onRetry: () => retried = true);

        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-9");
        cut.Markup.ShouldContain("Nope");
        cut.Find("button:not([aria-label])").Click();

        retried.ShouldBeTrue();
    }

    [Fact]
    public void Reading_value_of_a_failed_result_throws()
    {
        Should.Throw<InvalidOperationException>(() => ApiResult<string>.Fail("X", "y").Value);
    }
}
