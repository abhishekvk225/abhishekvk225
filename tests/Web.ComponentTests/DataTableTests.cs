using NexaVerify.Contracts.Common;

namespace NexaVerify.Web.ComponentTests;

public class DataTableTests : UiTestBase
{
    private sealed record Row(int Id, string Name);

    private static readonly IReadOnlyList<DataColumn<Row>> Columns =
    [
        DataColumn<Row>.Text("Name", r => r.Name, "name"),
        DataColumn<Row>.Text("Id", r => r.Id.ToString(), "id", numeric: true),
    ];

    private static ApiResult<PagedResult<Row>> Page(PageRequest req, int total)
    {
        var items = Enumerable.Range((req.Page - 1) * req.PageSize + 1, Math.Min(req.PageSize, Math.Max(0, total - (req.Page - 1) * req.PageSize)))
            .Select(i => new Row(i, $"Row {i}")).ToList();
        return ApiResult<PagedResult<Row>>.Ok(new PagedResult<Row>(items, req.Page, req.PageSize, total));
    }

    private IRenderedComponent<DataTable<Row>> RenderTable(Func<PageRequest, Task<ApiResult<PagedResult<Row>>>> load, string? search = null)
    {
        Render<MudPopoverProvider>();
        return Render<DataTable<Row>>(p => p
            .Add(x => x.Columns, Columns)
            .Add(x => x.LoadData, load)
            .Add(x => x.PageSize, 10)
            .Add(x => x.Search, search));
    }

    [Fact]
    public void Loads_first_page_and_renders_rows_in_table_and_card_list()
    {
        var requests = new List<PageRequest>();
        var cut = RenderTable(r => { requests.Add(r); return Task.FromResult(Page(r, 25)); });

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(10));
        requests[0].Page.ShouldBe(1);
        requests[0].PageSize.ShouldBe(10);
        cut.FindAll("[role=listitem]").Count.ShouldBe(10);
        cut.Find("[data-testid=range-text]").TextContent.ShouldBe("Showing 1–10 of 25");
    }

    [Fact]
    public void Next_and_previous_invoke_the_paging_callback_with_the_right_page()
    {
        var requests = new List<PageRequest>();
        var cut = RenderTable(r => { requests.Add(r); return Task.FromResult(Page(r, 25)); });
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(10));

        cut.Find("[data-testid=next-page]").Click();
        cut.WaitForAssertion(() => requests.Count.ShouldBe(2));
        requests[1].Page.ShouldBe(2);
        cut.WaitForAssertion(() => cut.Find("[data-testid=range-text]").TextContent.ShouldBe("Showing 11–20 of 25"));

        cut.Find("[data-testid=prev-page]").Click();
        cut.WaitForAssertion(() => requests.Count.ShouldBe(3));
        requests[2].Page.ShouldBe(1);
    }

    [Fact]
    public void Prev_is_disabled_on_first_page_and_next_on_last()
    {
        var cut = RenderTable(r => Task.FromResult(Page(r, 5)));

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(5));
        cut.Find("[data-testid=prev-page]").HasAttribute("disabled").ShouldBeTrue();
        cut.Find("[data-testid=next-page]").HasAttribute("disabled").ShouldBeTrue();
    }

    [Fact]
    public void Clicking_a_sortable_header_requests_sort_asc_then_desc()
    {
        var requests = new List<PageRequest>();
        var cut = RenderTable(r => { requests.Add(r); return Task.FromResult(Page(r, 25)); });
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(10));

        cut.FindAll("th button")[0].Click();
        cut.WaitForAssertion(() => requests.Count.ShouldBe(2));
        requests[1].Sort.ShouldBe("name:asc");
        cut.Find("th[aria-sort=ascending]").ShouldNotBeNull();

        cut.FindAll("th button")[0].Click();
        cut.WaitForAssertion(() => requests.Count.ShouldBe(3));
        requests[2].Sort.ShouldBe("name:desc");
    }

    [Fact]
    public void Changing_search_reloads_from_page_one()
    {
        var requests = new List<PageRequest>();
        var cut = RenderTable(r => { requests.Add(r); return Task.FromResult(Page(r, 25)); });
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(10));
        cut.Find("[data-testid=next-page]").Click();
        cut.WaitForAssertion(() => requests.Count.ShouldBe(2));

        cut.Render(p => p.Add(x => x.Search, "abc"));

        cut.WaitForAssertion(() => requests.Count.ShouldBe(3));
        requests[2].Search.ShouldBe("abc");
        requests[2].Page.ShouldBe(1);
    }

    [Fact]
    public void Shows_empty_state_when_there_are_no_rows()
    {
        var cut = RenderTable(r => Task.FromResult(Page(r, 0)));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=empty-state]").Count.ShouldBe(1));
        cut.FindAll("table").ShouldBeEmpty();
    }

    [Fact]
    public void Shows_no_results_wording_when_searching()
    {
        var cut = RenderTable(r => Task.FromResult(Page(r, 0)), search: "zzz");

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("No results found"));
    }

    [Fact]
    public void Shows_error_state_with_correlation_id_and_retry_reloads()
    {
        var calls = 0;
        var cut = RenderTable(r =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? ApiResult<PagedResult<Row>>.Fail("INTERNAL_ERROR", "Boom", "corr-123", 500)
                : Page(r, 3));
        });

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=error-state]").Count.ShouldBe(1));
        cut.Find("[data-testid=correlation-id]").TextContent.ShouldBe("corr-123");

        cut.Find("[data-testid=error-state] button:not([aria-label])").Click();

        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(3));
    }

    [Fact]
    public void A_throwing_loader_becomes_an_error_state()
    {
        var cut = RenderTable(_ => throw new InvalidOperationException("secret detail"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=error-state]").Count.ShouldBe(1));
        cut.Markup.ShouldNotContain("secret detail");
    }

    [Fact]
    public void Shows_skeleton_while_first_load_is_pending()
    {
        var tcs = new TaskCompletionSource<ApiResult<PagedResult<Row>>>();
        var cut = RenderTable(_ => tcs.Task);

        cut.FindAll("[data-testid=skeleton-table]").Count.ShouldBe(1);

        tcs.SetResult(Page(new PageRequest { PageSize = 10 }, 2));
        cut.WaitForAssertion(() => cut.FindAll("table tbody tr").Count.ShouldBe(2));
    }
}
