using PaymentsModule.Models;

namespace PaymentsModule.Tests;

public class SalesHistoryQueryValidatorTests
{
    private readonly SalesHistoryQueryValidator _validator = new();

    [Fact]
    public void Defaults_are_page_one_of_ten_and_valid()
    {
        var query = new SalesHistoryQuery();

        Assert.Equal((1, 10), (query.PageIndex, query.PageSize));
        Assert.True(_validator.Validate(query).IsValid);
    }

    [Fact]
    public void Accepts_valid_date_range_where_start_is_before_end()
    {
        var query = new SalesHistoryQuery
        {
            StartDate = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            EndDate = DateTimeOffset.Parse("2026-09-30T23:59:59Z"),
            PageIndex = 1,
            PageSize = 50
        };

        Assert.True(_validator.Validate(query).IsValid);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z"), query.ToFilter().StartDate);
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T23:59:59Z"), query.ToFilter().EndDate);
    }

    [Fact]
    public void Rejects_a_start_date_equal_to_or_after_end_date()
    {
        var equalDatesQuery = new SalesHistoryQuery
        {
            StartDate = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            EndDate = DateTimeOffset.Parse("2026-09-01T00:00:00Z")
        };

        var greaterDateQuery = new SalesHistoryQuery
        {
            StartDate = DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            EndDate = DateTimeOffset.Parse("2026-09-01T00:00:00Z")
        };

        var resultEqual = _validator.Validate(equalDatesQuery);
        var resultGreater = _validator.Validate(greaterDateQuery);

        Assert.False(resultEqual.IsValid);
        Assert.Contains(resultEqual.Errors, e => e.ErrorMessage.Contains("La fecha de inicio debe ser anterior a la fecha de fin"));

        Assert.False(resultGreater.IsValid);
        Assert.Contains(resultGreater.Errors, e => e.ErrorMessage.Contains("La fecha de inicio debe ser anterior a la fecha de fin"));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public void Rejects_paging_out_of_range(int pageIndex, int pageSize) =>
        Assert.False(_validator.Validate(new SalesHistoryQuery { PageIndex = pageIndex, PageSize = pageSize }).IsValid);
}
