using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Controllers;
using PaymentsModule.Data;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// Runs against a real SQL Server: `docker compose up -d sqlserver` locally, a service container in CI.
public class PrintTicketTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True";

    // Far from the values the folio sequence hands out to the charge tests.
    private static long _numbers = 8_000_000_000;

    private readonly FailOnQuery _failure = new();
    private readonly ServiceProvider _provider;

    public PrintTicketTests()
    {
        _provider = new ServiceCollection()
            .AddLogging()
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

    // Scenario 1.
    [Fact]
    public async Task Existing_folio_returns_header_body_and_footer_from_the_sale_history()
    {
        var soldAt = new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);
        var sale = await InsertSaleAsync(soldAt, TransactionStatus.Completed);

        var result = await GetAsync(sale.Folio);

        var ok = Assert.IsType<OkObjectResult>(result);
        var ticket = Assert.IsType<PrintTicketResponse>(ok.Value);
        Assert.Equal(new PrintTicketHeader(sale.Folio, soldAt, sale.CashShiftId, sale.DinningSessionId), ticket.Header);
        Assert.Equal(
            new[]
            {
                new PrintTicketLine("Corte Ribeye", 1, 500.00m, 500.00m),
                new PrintTicketLine("Agua de horchata", 2, 35.50m, 71.00m)
            },
            ticket.Lines);
        var footer = ticket.Footer;
        Assert.Equal((571.00m, 91.36m, 60.00m, 722.36m), (footer.Subtotal, footer.Taxes, footer.Tip, footer.Total));
        Assert.Equal(
            new[] { new PrintTicketPayment(PaymentMethod.CreditCard, 400.00m), new PrintTicketPayment(PaymentMethod.Cash, 322.36m) },
            footer.Payments);
        Assert.Equal(TransactionStatus.Completed, footer.Status);
    }

    // Wire contract: Spanish snake_case names, enum labels and decimals with their two decimal places.
    [Fact]
    public async Task Ticket_serializes_to_the_agreed_json_contract()
    {
        var sale = await InsertSaleAsync(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero), TransactionStatus.Cancelled);

        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value);

        Assert.Equal(
            $$$"""
            {"cabecera":{"folio":"{{{sale.Folio}}}","fecha_impresion":"2026-09-29T18:00:00+00:00","turno_caja_id":"{{{sale.CashShiftId}}}","dinning_session_id":"{{{sale.DinningSessionId}}}"},"cuerpo":[{"nombre_platillo":"Corte Ribeye","cantidad":1,"precio_unitario":500.00,"importe":500.00},{"nombre_platillo":"Agua de horchata","cantidad":2,"precio_unitario":35.50,"importe":71.00}],"pie":{"subtotal":571.00,"impuestos":91.36,"propina":60.00,"total":722.36,"pagos":[{"metodo_pago":"TARJETA_CREDITO","monto":400.00},{"metodo_pago":"EFECTIVO","monto":322.36}],"estado":"CANCELADO"}}
            """,
            json);
    }

    // Scenario 2.
    [Fact]
    public async Task Unknown_folio_returns_not_found()
    {
        var result = await GetAsync("TX-00000000-000000");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(notFound.Value);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    // Scenario 3: the exception reaches the global handler (UseExceptionHandler), which answers 500 without internals.
    [Fact]
    public async Task Database_timeout_aborts_the_ticket_and_propagates_to_the_exception_handler()
    {
        var sale = await InsertSaleAsync(DateTimeOffset.UtcNow, TransactionStatus.Completed);
        _failure.Enabled = true;

        await Assert.ThrowsAsync<SimulatedTimeout>(() => GetAsync(sale.Folio));
    }

    // Scenario 4: five simultaneous reprints get exactly the same document.
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

    // Order guarantee: lines and payments come back in the order they were charged (EF Core sequential GUIDs),
    // with enough rows that an accidental order would not pass by chance.
    [Fact]
    public async Task Lines_and_payments_keep_the_order_they_were_charged_in()
    {
        var dishes = Enumerable.Range(1, 12).Select(n => $"Platillo {n:D2}").ToArray();
        var methods = new[] { PaymentMethod.DebitCard, PaymentMethod.CreditCard, PaymentMethod.Cash };
        var sale = await InsertSaleAsync(
            DateTimeOffset.UtcNow,
            TransactionStatus.Completed,
            dishes.Select(d => new TransactionItem { DishName = d, Quantity = 1, UnitPrice = 10.00m, Amount = 10.00m }).ToList(),
            methods.Select(m => new PaymentBreakdown { Method = m, Amount = 40.00m }).ToList());

        var ticket = Assert.IsType<PrintTicketResponse>(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value);

        Assert.Equal(dishes, ticket.Lines.Select(l => l.DishName));
        Assert.Equal(methods, ticket.Footer.Payments.Select(p => p.Method));
    }

    // fecha_impresion is the sale date, not the moment of the query: reprints made at different times are identical.
    [Fact]
    public async Task Reprints_at_different_times_keep_the_sale_date()
    {
        var soldAt = new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);
        var sale = await InsertSaleAsync(soldAt, TransactionStatus.Completed);

        var first = Assert.IsType<PrintTicketResponse>(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var second = Assert.IsType<PrintTicketResponse>(Assert.IsType<OkObjectResult>(await GetAsync(sale.Folio)).Value);

        Assert.Equal(soldAt, first.Header.PrintedAt);
        Assert.Equal(soldAt, second.Header.PrintedAt);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void Endpoint_requires_an_authenticated_caller()
    {
        Assert.NotNull(typeof(PrintTicketController).GetCustomAttribute<AuthorizeAttribute>());
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

    // A sale shaped like ChargeService persists it: items as plain text, electronic payments first.
    private async Task<PaymentTransaction> InsertSaleAsync(
        DateTimeOffset soldAt,
        TransactionStatus status,
        List<TransactionItem>? items = null,
        List<PaymentBreakdown>? payments = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var shift = new CashShift { CashierId = $"cashier-{Guid.NewGuid()}", InitialFund = 500m };
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
            Subtotal = 571.00m,
            Taxes = 91.36m,
            Tip = 60.00m,
            Total = 722.36m,
            Status = status,
            CreatedAt = soldAt,
            Items = items ?? new List<TransactionItem>
            {
                new() { DishName = "Corte Ribeye", Quantity = 1, UnitPrice = 500.00m, Amount = 500.00m },
                new() { DishName = "Agua de horchata", Quantity = 2, UnitPrice = 35.50m, Amount = 71.00m }
            },
            Payments = payments ?? new List<PaymentBreakdown>
            {
                new() { Method = PaymentMethod.CreditCard, Amount = 400.00m, AuthorizationReference = "AUTH-1" },
                new() { Method = PaymentMethod.Cash, Amount = 322.36m }
            }
        };
        db.Transactions.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }

    private sealed class SimulatedTimeout() : DbException("Simulated SQL Server timeout.");

    // Simulates a SQL Server timeout while the ticket query runs (PMC-14 scenario 3).
    private sealed class FailOnQuery : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            Enabled ? throw new SimulatedTimeout() : base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
