using NexaVerify.Contracts.Common;

namespace NexaVerify.Application.UnitTests;

public class PagingTests
{
    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, -1, 1, 1)]
    [InlineData(3, 1000, 3, PageRequest.MaxPageSize)]
    [InlineData(2, 50, 2, 50)]
    public void Normalize_clamps_page_and_size(int page, int size, int expectedPage, int expectedSize)
    {
        var normalized = new PageRequest { Page = page, PageSize = size }.Normalize();

        normalized.Page.ShouldBe(expectedPage);
        normalized.PageSize.ShouldBe(expectedSize);
    }

    [Fact]
    public void Skip_uses_clamped_values_so_abusive_input_cannot_overshoot()
    {
        new PageRequest { Page = 3, PageSize = 100000 }.Skip.ShouldBe(2 * PageRequest.MaxPageSize);
        new PageRequest { Page = -4, PageSize = 10 }.Skip.ShouldBe(0);
    }

    [Fact]
    public void Extreme_page_values_cannot_overflow_skip()
    {
        var request = new PageRequest { Page = int.MaxValue, PageSize = int.MaxValue };

        request.Skip.ShouldBe((PageRequest.MaxPage - 1) * PageRequest.MaxPageSize);
        request.Skip.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData(0, 25, 0)]
    [InlineData(1, 25, 1)]
    [InlineData(26, 25, 2)]
    public void Total_pages_rounds_up(int total, int size, int pages)
    {
        new PagedResult<int>([], 1, size, total).TotalPages.ShouldBe(pages);
    }
}
