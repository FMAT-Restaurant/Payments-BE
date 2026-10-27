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
    public void Accepts_every_filter_together()
    {
        var query = new SalesHistoryQuery
        {
            From = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
            To = DateTimeOffset.Parse("2026-09-30T23:59:59Z"),
            Status = "COMPLETADO",
            Method = "TARJETA_CREDITO",
            PageIndex = 1,
            PageSize = 50
        };

        Assert.True(_validator.Validate(query).IsValid);
        Assert.Equal((TransactionStatus.Completed, PaymentMethod.CreditCard), (query.ToFilter().Status, query.ToFilter().Method));
    }

    [Fact]
    public void Rejects_a_start_date_after_the_end_date()
    {
        var query = new SalesHistoryQuery
        {
            From = DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            To = DateTimeOffset.Parse("2026-09-01T00:00:00Z")
        };

        var result = _validator.Validate(query);

        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("date range is invalid"));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public void Rejects_paging_out_of_range(int pageIndex, int pageSize) =>
        Assert.False(_validator.Validate(new SalesHistoryQuery { PageIndex = pageIndex, PageSize = pageSize }).IsValid);

    [Theory]
    [InlineData("PENDIENTE")]
    [InlineData("")]
    public void Rejects_unknown_status(string status) =>
        Assert.False(_validator.Validate(new SalesHistoryQuery { Status = status }).IsValid);

    [Theory]
    [InlineData("CARD")]
    [InlineData("EFECTIVO_Y_TARJETA")]
    public void Rejects_unknown_payment_method(string method) =>
        Assert.False(_validator.Validate(new SalesHistoryQuery { Method = method }).IsValid);

    [Fact]
    public void Parses_wire_names_of_the_enums()
    {
        Assert.True(WireEnum.TryParse<TransactionStatus>("CANCELADO", out var status));
        Assert.Equal(TransactionStatus.Cancelled, status);
        Assert.True(WireEnum.TryParse<PaymentMethod>("TARJETA_DEBITO", out var method));
        Assert.Equal(PaymentMethod.DebitCard, method);
        Assert.False(WireEnum.TryParse<PaymentMethod>(null, out _));
    }
}
