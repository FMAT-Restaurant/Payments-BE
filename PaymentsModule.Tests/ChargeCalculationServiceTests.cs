using System.Reflection;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// Pure domain math of PMC-13: no database, no I/O.
public class ChargeCalculationServiceTests
{
    private readonly ChargeCalculationService _calculator = new();

    [Fact]
    public void Cash_above_the_amount_due_returns_the_difference_as_change()
    {
        var result = _calculator.Calculate([Cash(100m)], 80m);

        Assert.Equal(ChargeCalculationStatus.Covered, result.Status);
        Assert.Equal(20m, result.ChangeGiven);
        var cash = Assert.Single(result.Breakdown);
        Assert.Equal((PaymentMethod.Cash, 80m), (cash.Method, cash.Amount));
    }

    [Fact]
    public void Exact_payment_gives_no_change()
    {
        var result = _calculator.Calculate([Cash(80m)], 80m);

        Assert.Equal((ChargeCalculationStatus.Covered, 0m), (result.Status, result.ChangeGiven));
        Assert.Equal(80m, Assert.Single(result.Breakdown).Amount);
    }

    [Fact]
    public void Several_cash_payments_are_capped_in_order()
    {
        var result = _calculator.Calculate([Cash(50m), Cash(50m), Cash(20m)], 80m);

        Assert.Equal(40m, result.ChangeGiven);
        Assert.Equal(new[] { 50m, 30m }, result.Breakdown.Select(b => b.Amount));
    }

    [Theory]
    [InlineData(PaymentMethod.CreditCard)]
    [InlineData(PaymentMethod.DebitCard)]
    public void Mixed_payment_lets_cash_absorb_the_excess(PaymentMethod card)
    {
        // PMC-13-P1: card 50 + cash 100 over 120 is valid; the card is kept whole and cash pays the rest.
        var result = _calculator.Calculate([Cash(100m), Card(card, 50m)], 120m);

        Assert.Equal(ChargeCalculationStatus.Covered, result.Status);
        Assert.Equal(30m, result.ChangeGiven);
        Assert.Equal(new[] { (card, 50m), (PaymentMethod.Cash, 70m) }, result.Breakdown.Select(b => (b.Method, b.Amount)));
        Assert.Equal("AUTH-1", result.Breakdown[0].AuthorizationReference);
    }

    [Fact]
    public void Card_covering_everything_returns_the_whole_cash_as_change()
    {
        // PMC-13-P6: the cash contributes nothing to the breakdown and goes back whole.
        var result = _calculator.Calculate([Card(PaymentMethod.CreditCard, 100m), Cash(50m)], 100m);

        Assert.Equal((ChargeCalculationStatus.Covered, 50m), (result.Status, result.ChangeGiven));
        var card = Assert.Single(result.Breakdown);
        Assert.Equal((PaymentMethod.CreditCard, 100m), (card.Method, card.Amount));
    }

    [Theory]
    [InlineData(PaymentMethod.CreditCard)]
    [InlineData(PaymentMethod.DebitCard)]
    public void Electronic_payments_above_the_amount_due_are_rejected(PaymentMethod card)
    {
        var result = _calculator.Calculate([Card(card, 160m)], 150m);

        Assert.Equal(ChargeCalculationStatus.ElectronicOverpayment, result.Status);
        Assert.Empty(result.Breakdown);
        Assert.Equal(0m, result.ChangeGiven);
    }

    [Fact]
    public void Several_cards_adding_up_above_the_amount_due_are_rejected_even_with_cash()
    {
        var result = _calculator.Calculate([Card(PaymentMethod.CreditCard, 80m), Card(PaymentMethod.DebitCard, 80m), Cash(10m)], 150m);

        Assert.Equal(ChargeCalculationStatus.ElectronicOverpayment, result.Status);
    }

    [Fact]
    public void Missing_money_reports_the_deficit()
    {
        var result = _calculator.Calculate([Cash(60m), Card(PaymentMethod.DebitCard, 10m)], 80m);

        Assert.Equal((ChargeCalculationStatus.InsufficientFunds, 10m), (result.Status, result.Deficit));
        Assert.Empty(result.Breakdown);
    }

    [Theory]
    [InlineData("100.00", "80.00", "20.00")]
    [InlineData("100.00", "99.99", "0.01")]
    [InlineData("0.30", "0.10", "0.20")]
    [InlineData("500.00", "123.45", "376.55")]
    public void Change_is_exact_with_decimal_arithmetic(string paid, string due, string change)
    {
        var result = _calculator.Calculate([Cash(decimal.Parse(paid))], decimal.Parse(due));

        Assert.Equal(decimal.Parse(change), result.ChangeGiven);
        Assert.Equal(decimal.Parse(due), result.Breakdown.Sum(b => b.Amount));
    }

    [Fact]
    public void Cents_split_over_several_payments_add_up_exactly()
    {
        // 0.1 + 0.2 is not 0.3 in binary floating point; with decimal it is.
        var result = _calculator.Calculate([Cash(0.10m), Cash(0.20m)], 0.30m);

        Assert.Equal((ChargeCalculationStatus.Covered, 0m), (result.Status, result.ChangeGiven));
        Assert.Equal(0.30m, result.Breakdown.Sum(b => b.Amount));
    }

    [Theory]
    [MemberData(nameof(RandomCharges))]
    public void Breakdown_always_equals_the_amount_due_and_change_is_the_rest(decimal[] cash, decimal[] cards, decimal due)
    {
        var payments = cash.Select(Cash).Concat(cards.Select(c => Card(PaymentMethod.CreditCard, c))).ToList();

        var result = _calculator.Calculate(payments, due);

        Assert.Equal(ChargeCalculationStatus.Covered, result.Status);
        Assert.Equal(due, result.Breakdown.Sum(b => b.Amount));
        Assert.Equal(payments.Sum(p => p.Amount) - due, result.ChangeGiven);
        Assert.All(result.Breakdown, b => Assert.True(b.Amount > 0m));
        // Card amounts are never capped.
        Assert.Equal(cards.Sum(), result.Breakdown.Where(b => b.Method != PaymentMethod.Cash).Sum(b => b.Amount));
    }

    public static TheoryData<decimal[], decimal[], decimal> RandomCharges()
    {
        var random = new Random(13);
        var data = new TheoryData<decimal[], decimal[], decimal>();
        for (var i = 0; i < 25; i++)
        {
            var cards = Enumerable.Range(0, random.Next(0, 3)).Select(_ => Cents(random, 1, 20_000)).ToArray();
            var due = cards.Sum() + Cents(random, 0, 50_000);
            var missing = due - cards.Sum();
            // Enough cash to cover what the cards leave, plus some change.
            var cash = missing == 0m ? Array.Empty<decimal>() : new[] { missing + Cents(random, 0, 10_000) };
            data.Add(cash, cards, due);
        }

        return data;
    }

    [Fact]
    public void Public_contract_uses_decimal_only()
    {
        // RNF-05: no float/double anywhere in the calculation contract or the response DTO.
        var types = new[] { typeof(IChargeCalculationService), typeof(ChargeCalculation), typeof(ChargeResponse), typeof(PaymentRequest) };
        var members = types.SelectMany(t => t.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
            .Concat(t.GetProperties().Select(p => p.PropertyType)));

        Assert.DoesNotContain(members, t => t == typeof(float) || t == typeof(double));
        Assert.Equal(typeof(decimal), typeof(ChargeResponse).GetProperty(nameof(ChargeResponse.ChangeGiven), BindingFlags.Public | BindingFlags.Instance)!.PropertyType);
    }

    private static decimal Cents(Random random, int min, int max) => random.Next(min, max) / 100m;

    private static PaymentRequest Cash(decimal amount) => new(PaymentMethod.Cash, amount, null);

    private static PaymentRequest Card(PaymentMethod method, decimal amount) => new(method, amount, "AUTH-1");
}
