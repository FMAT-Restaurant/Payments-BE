using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Controllers;
using PaymentsModule.Data;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// Runs against a real SQL Server: `docker compose up -d sqlserver` locally, a service container in CI.
public class PrintTicketTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString();

    // Far from the values the folio sequence hands out to the charge tests.
    private static long _numbers = 8_000_000_000;

    private readonly FailingQueries _failure = new();
    private readonly ServiceProvider _provider;

    public PrintTicketTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Restaurant:Name"] = "Restaurante FMAT"
            })
            .Build();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(_failure))
            .AddScoped<PrintTicketService>()
            .BuildServiceProvider(true);
    }

    public async Task InitializeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await ChargeServiceTests.EnsureMigratedAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    // Scenario 1: Existing folio returns flat browser-print DTO.
    [Fact]
    public async Task Existing_folio_returns_print_ticket_from_the_sale_history()
    {
        var soldAt = new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);
        var sale = await InsertSaleAsync(soldAt, TransactionStatus.Completed);

        var result = await GetAsync(sale.Folio);

        var ok = Assert.IsType<OkObjectResult>(result);
        var ticket = Assert.IsType<PrintTicketResponse>(ok.Value);
        Assert.Equal("Restaurante FMAT", ticket.RestaurantName);
        Assert.Equal(sale.Folio, ticket.Folio);
        Assert.Equal(soldAt, ticket.PaidAt);
        Assert.Equal("Mesa 04", ticket.TableName);
        Assert.Equal("Juan Pérez", ticket.CashierName);
        Assert.Equal(
            new[]
            {
                new PrintTicketLine("Corte Ribeye", 1, 500.00m, 500.00m),
                new PrintTicketLine("Agua de horchata", 2, 35.50m, 71.00m)
            },
            ticket.Items);
        Assert.Equal((571.00m, 91.36m, 60.00m, 722.36m, 50.00m), (ticket.Subtotal, ticket.Taxes, ticket.Tip, ticket.Total, ticket.ChangeGiven));
        Assert.Equal(
            new[]
            {
                new PrintTicketPayment(PaymentMethod.CreditCard, 400.00m, "AUTH-987654"),
                new PrintTicketPayment(PaymentMethod.Cash, 322.36m, null)
            },
            ticket.Payments);
    }

    // Wire contract: JSON property names in snake_case, enum labels and decimals with two decimal places.
    [Fact]
    public async Task Ticket_serializes_to_the_agreed_json_contract()
    {
        var soldAt = new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);
        var sale = await InsertSaleAsync(soldAt, TransactionStatus.Completed);

        var serializerOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value, serializerOptions);

        Assert.Equal(
            $$$"""
            {"restaurant_name":"Restaurante FMAT","folio":"{{{sale.Folio}}}","paid_at":"2026-11-07T18:00:00+00:00","table_name":"Mesa 04","cashier_name":"Juan Pérez","items":[{"dish_name":"Corte Ribeye","quantity":1,"unit_price":500.00,"amount":500.00},{"dish_name":"Agua de horchata","quantity":2,"unit_price":35.50,"amount":71.00}],"subtotal":571.00,"taxes":91.36,"tip":60.00,"total":722.36,"payments":[{"method":"TARJETA_CREDITO","amount":400.00,"authorization_reference":"AUTH-987654"},{"method":"EFECTIVO","amount":322.36,"authorization_reference":null}],"change_given":50.00}
            """,
            json);
    }

    // Scenario 2: Unknown folio returns 404 TRANSACTION_NOT_FOUND.
    [Fact]
    public async Task Unknown_folio_returns_not_found()
    {
        var result = await GetAsync("TX-00000000-000000");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("TRANSACTION_NOT_FOUND", error.Code);
        Assert.Equal("El ticket 'TX-00000000-000000' no existe", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_or_whitespace_folio_returns_not_found(string folio)
    {
        var result = await GetAsync(folio);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("TRANSACTION_NOT_FOUND", error.Code);
    }

    // Scenario 3, service side: the exception propagates out of the controller untouched. The 500 answered by
    // UseExceptionHandler is asserted over HTTP in PrintTicketHttpTests.
    [Fact]
    public async Task Database_timeout_aborts_the_ticket_and_propagates_to_the_exception_handler()
    {
        var sale = await InsertSaleAsync(DateTimeOffset.UtcNow, TransactionStatus.Completed);
        _failure.FailQueriesWith(sale.Folio);

        await Assert.ThrowsAsync<SimulatedDatabaseTimeout>(() => GetAsync(sale.Folio));
    }

    // Scenario 4: five simultaneous reprints get exactly the same document without blocking.
    [Fact]
    public async Task Concurrent_reprints_return_identical_tickets()
    {
        var sale = await InsertSaleAsync(DateTimeOffset.UtcNow, TransactionStatus.Completed);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => GetAsync(sale.Folio)));

        var documents = results
            .Select(r => JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(r).Value))
            .ToList();
        Assert.Single(documents.Distinct());
        Assert.Contains(sale.Folio, documents[0]);
    }

    // Order guarantee: items and payments come back in insertion order (EF Core sequential GUIDs).
    [Fact]
    public async Task Items_and_payments_keep_the_order_they_were_charged_in()
    {
        var dishes = Enumerable.Range(1, 12).Select(n => $"Platillo {n:D2}").ToArray();
        var methods = new[] { PaymentMethod.DebitCard, PaymentMethod.CreditCard, PaymentMethod.Cash };
        var sale = await InsertSaleAsync(
            DateTimeOffset.UtcNow,
            TransactionStatus.Completed,
            dishes.Select(d => new TransactionItem { DishName = d, Quantity = 1, UnitPrice = 10.00m, Amount = 10.00m }).ToList(),
            methods.Select((m, i) => new PaymentBreakdown { Method = m, Amount = 40.00m, AuthorizationReference = $"AUTH-{i}" }).ToList());

        var ticket = Assert.IsType<PrintTicketResponse>(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value);

        Assert.Equal(dishes, ticket.Items.Select(l => l.DishName));
        Assert.Equal(methods, ticket.Payments.Select(p => p.Method));
    }

    // paid_at is the sale date, not the moment of the query: reprints made at different times are identical.
    [Fact]
    public async Task Reprints_at_different_times_keep_the_sale_date()
    {
        var soldAt = new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);
        var sale = await InsertSaleAsync(soldAt, TransactionStatus.Completed);

        var first = Assert.IsType<PrintTicketResponse>(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var second = Assert.IsType<PrintTicketResponse>(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value);

        Assert.Equal(soldAt, first.PaidAt);
        Assert.Equal(soldAt, second.PaidAt);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    private async Task<IActionResult> GetAsync(string folio)
    {
        await using var scope = _provider.CreateAsyncScope();
        var controller = new PrintTicketController(scope.ServiceProvider.GetRequiredService<PrintTicketService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return await controller.GetPrintTicket(folio, CancellationToken.None);
    }

    private async Task<PaymentTransaction> InsertSaleAsync(
        DateTimeOffset soldAt,
        TransactionStatus status,
        List<TransactionItem>? items = null,
        List<PaymentBreakdown>? payments = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await SeedSaleAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>(), soldAt, status, items, payments);
    }

    // A sale shaped like ChargeService persists it: items as plain text, electronic payments first.
    // Also used by PrintTicketHttpTests, so both suites draw folio numbers from the same counter.
    internal static async Task<PaymentTransaction> SeedSaleAsync(
        PaymentsDbContext db,
        DateTimeOffset soldAt,
        TransactionStatus status,
        List<TransactionItem>? items = null,
        List<PaymentBreakdown>? payments = null)
    {
        var shift = new CashShift { CashierId = "Juan Pérez", Status = CashShiftStatus.Closed, InitialFund = 500m };
        db.CashShifts.Add(shift);
        await db.SaveChangesAsync();

        var number = Interlocked.Increment(ref _numbers);
        var sale = new PaymentTransaction
        {
            Number = number,
            Folio = $"TX-{soldAt:yyyyMMdd}-{number:D6}",
            DinningSessionId = Guid.NewGuid(),
            CashShiftId = shift.Id,
            CashierId = shift.CashierId,
            TableName = "Mesa 04",
            Subtotal = 571.00m,
            Taxes = 91.36m,
            Tip = 60.00m,
            Total = 722.36m,
            ChangeGiven = 50.00m,
            Status = status,
            CreatedAt = soldAt,
            Items = items ?? new List<TransactionItem>
            {
                new() { DishName = "Corte Ribeye", Quantity = 1, UnitPrice = 500.00m, Amount = 500.00m },
                new() { DishName = "Agua de horchata", Quantity = 2, UnitPrice = 35.50m, Amount = 71.00m }
            },
            Payments = payments ?? new List<PaymentBreakdown>
            {
                new() { Method = PaymentMethod.CreditCard, Amount = 400.00m, AuthorizationReference = "AUTH-987654" },
                new() { Method = PaymentMethod.Cash, Amount = 322.36m, AuthorizationReference = null }
            }
        };
        db.Transactions.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }
}
