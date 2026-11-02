using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Testing;
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
public class ChargeServiceTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True";

    private static readonly SemaphoreSlim MigrateOnce = new(1, 1);
    private static bool _migrated;

    private readonly FailOnBreakdownInsert _failure = new();
    private readonly FakePaymentGateway _gateway = new();
    private readonly ServiceProvider _provider;

    public ChargeServiceTests()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(_failure))
            .AddSingleton<IPaymentGateway>(_gateway)
            .AddScoped<PendingBillService>()
            .AddScoped<ChargeService>()
            .AddScoped<IValidator<ChargeRequest>, ChargeRequestValidator>()
            .AddMassTransitTestHarness(x =>
            {
                // Stands in for Órdenes/Cocina, proving the event actually leaves the outbox through the broker.
                x.AddConsumer<PaymentCompletedListener>();
                x.AddEntityFrameworkOutbox<PaymentsDbContext>(o =>
                {
                    o.UseSqlServer();
                    o.UseBusOutbox();
                    o.QueryDelay = TimeSpan.FromMilliseconds(100);
                });
            })
            .BuildServiceProvider(true);
    }

    private ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    public async Task InitializeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            await EnsureMigratedAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
        }

        await Harness.Start();
    }

    internal static async Task EnsureMigratedAsync(PaymentsDbContext db)
    {
        await MigrateOnce.WaitAsync();
        try
        {
            if (!_migrated)
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.MigrateAsync();
                _migrated = true;
            }
        }
        finally
        {
            MigrateOnce.Release();
        }
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Exact_cash_payment_persists_history_and_emits_event()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var result = await ChargeAsync(Request(session, Cash(250m)), cashier);

        Assert.Equal(ChargeOutcome.Completed, result.Outcome);
        Assert.Matches(@"^TX-\d{8}-\d{6}$", result.Response!.Folio);
        Assert.Equal(0m, result.Response.ChangeGiven);

        var saved = await FindAsync(session);
        Assert.NotNull(saved);
        Assert.Equal(TransactionStatus.Completed, saved.Status);
        Assert.Equal(250m, saved.Total);
        Assert.Equal(cashier, saved.CashierId);
        var item = Assert.Single(saved.Items);
        Assert.Equal("Tacos al Pastor", item.DishName);
        var payment = Assert.Single(saved.Payments);
        Assert.Equal((PaymentMethod.Cash, 250m), (payment.Method, payment.Amount));

        Assert.True(await Harness.Consumed.Any<PaymentCompletedEvent>(m =>
            m.Context.Message.DinningSessionId == session && m.Context.Message.Folio == saved.Folio));
    }

    [Fact]
    public async Task Insufficient_funds_returns_deficit_without_persisting_or_publishing()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var result = await ChargeAsync(Request(session, Cash(200m)), cashier);

        Assert.Equal(ChargeOutcome.InsufficientFunds, result.Outcome);
        Assert.Equal(50m, result.Deficit);
        Assert.Null(await FindAsync(session));
        Assert.Equal(0, await OutboxCountAsync(session));
    }

    [Fact]
    public async Task Database_failure_rolls_back_everything_and_emits_nothing()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);
        _failure.Enabled = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => ChargeAsync(Request(session, Cash(250m)), cashier));

        _failure.Enabled = false;
        Assert.Null(await FindAsync(session));
        Assert.Equal(0, await OutboxCountAsync(session));
    }

    [Fact]
    public async Task Duplicate_request_returns_already_paid_without_duplicating()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);
        await ChargeAsync(Request(session, Cash(250m)), cashier);

        var retry = await ChargeAsync(Request(session, Cash(250m)), cashier);

        Assert.Equal(ChargeOutcome.AlreadyPaid, retry.Outcome);
        Assert.Equal(1, await CountAsync(session));
    }

    [Fact]
    public async Task Concurrent_duplicates_charge_only_once()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ChargeAsync(Request(session, Cash(250m)), cashier)));

        Assert.Single(results, r => r.Outcome == ChargeOutcome.Completed);
        Assert.All(results.Where(r => r.Outcome != ChargeOutcome.Completed), r => Assert.Equal(ChargeOutcome.AlreadyPaid, r.Outcome));
        Assert.Equal(1, await CountAsync(session));
    }

    [Fact]
    public async Task Cash_excess_is_returned_as_change_and_not_stored_as_income()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);

        var result = await ChargeAsync(Request(session, Cash(100m)), cashier);

        Assert.Equal(20m, result.Response!.ChangeGiven);
        var saved = await FindAsync(session);
        Assert.Equal(80m, saved!.Total);
        Assert.Equal(80m, Assert.Single(saved.Payments).Amount);
    }

    [Fact]
    public async Task Tip_is_added_to_the_amount_due()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var shortByTip = await ChargeAsync(Request(session, Cash(250m)) with { Tip = 15m }, cashier);
        var covered = await ChargeAsync(Request(session, Cash(265m)) with { Tip = 15m }, cashier);

        Assert.Equal((ChargeOutcome.InsufficientFunds, 15m), (shortByTip.Outcome, shortByTip.Deficit));
        Assert.Equal(265m, covered.Response!.Total);
        Assert.Equal(15m, (await FindAsync(session))!.Tip);
    }

    [Fact]
    public async Task Card_payment_cannot_exceed_total()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 150m);

        var result = await ChargeAsync(Request(session, new PaymentRequest(PaymentMethod.DebitCard, 160m, "AUTH-1")), cashier);

        Assert.Equal(ChargeOutcome.ElectronicOverpayment, result.Outcome);
        Assert.Null(await FindAsync(session));
    }

    [Fact]
    public async Task Card_payment_is_charged_in_the_gateway_for_the_amount_applied()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var result = await ChargeAsync(RequestWith(session, Cash(100m), new PaymentRequest(PaymentMethod.CreditCard, 150m, "AUTH-1")), cashier);

        Assert.Equal(ChargeOutcome.Completed, result.Outcome);
        var charge = Assert.Single(_gateway.Charged);
        Assert.Equal((session, "AUTH-1", 150m), (charge.DinningSessionId, charge.AuthorizationReference, charge.Amount));
        Assert.Empty(_gateway.Reversed);
        Assert.Equal(2, (await FindAsync(session))!.Payments.Count);
    }

    [Fact]
    public async Task Declined_card_rejects_the_charge_and_reverses_the_cards_already_approved()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);
        _gateway.Decline("AUTH-B");

        var result = await ChargeAsync(
            RequestWith(session, new PaymentRequest(PaymentMethod.CreditCard, 100m, "AUTH-A"), new PaymentRequest(PaymentMethod.DebitCard, 150m, "AUTH-B")),
            cashier);

        Assert.Equal(ChargeOutcome.CardDeclined, result.Outcome);
        Assert.Equal("AUTH-A", Assert.Single(_gateway.Reversed).AuthorizationReference);
        Assert.Null(await FindAsync(session));
        Assert.Equal(0, await OutboxCountAsync(session));
    }

    [Fact]
    public async Task Gateway_outage_returns_unavailable_and_reverses_the_charge_in_doubt()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);
        _gateway.Unavailable = true;

        var result = await ChargeAsync(Request(session, new PaymentRequest(PaymentMethod.CreditCard, 250m, "AUTH-1")), cashier);

        Assert.Equal(ChargeOutcome.GatewayUnavailable, result.Outcome);
        Assert.Equal("AUTH-1", Assert.Single(_gateway.Reversed).AuthorizationReference);
        Assert.Null(await FindAsync(session));
    }

    [Fact]
    public async Task Database_failure_reverses_the_cards_charged_in_the_gateway()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);
        _failure.Enabled = true;

        await Assert.ThrowsAsync<DbUpdateException>(
            () => ChargeAsync(Request(session, new PaymentRequest(PaymentMethod.CreditCard, 250m, "AUTH-1")), cashier));

        _failure.Enabled = false;
        Assert.Equal("AUTH-1", Assert.Single(_gateway.Reversed).AuthorizationReference);
        Assert.Null(await FindAsync(session));
        Assert.Equal(0, await OutboxCountAsync(session));
    }

    [Fact]
    public async Task Cash_only_and_duplicate_requests_never_reach_the_gateway()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);
        await ChargeAsync(Request(session, Cash(250m)), cashier);

        var retry = await ChargeAsync(Request(session, new PaymentRequest(PaymentMethod.CreditCard, 250m, "AUTH-1")), cashier);

        Assert.Equal(ChargeOutcome.AlreadyPaid, retry.Outcome);
        Assert.Empty(_gateway.Charged);
    }

    [Fact]
    public async Task Controller_maps_gateway_outcomes_to_422_and_503()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);
        var card = new PaymentRequest(PaymentMethod.CreditCard, 250m, "AUTH-1");

        _gateway.Decline("AUTH-1");
        var declined = await ControllerChargeAsync(Request(session, card), cashier);
        _gateway.Unavailable = true;
        var unavailable = await ControllerChargeAsync(Request(session, card), cashier);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, declined.StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Card_payments_require_an_authorization_reference(string? reference)
    {
        var validator = new ChargeRequestValidator();

        Assert.False(validator.Validate(Request(Guid.NewGuid(), new PaymentRequest(PaymentMethod.CreditCard, 100m, reference))).IsValid);
        Assert.True(validator.Validate(Request(Guid.NewGuid(), Cash(100m))).IsValid);
    }

    [Fact]
    public async Task Rejects_cashier_without_open_shift()
    {
        var (session, _) = await ArrangeAsync(billTotal: 250m);

        var result = await ChargeAsync(Request(session, Cash(250m)), cashierId: "cashier-without-shift");

        Assert.Equal(ChargeOutcome.NoOpenShift, result.Outcome);
    }

    [Fact]
    public async Task Rejects_session_without_pending_bill()
    {
        var (_, cashier) = await ArrangeAsync(billTotal: 250m);

        var result = await ChargeAsync(Request(Guid.NewGuid(), Cash(250m)), cashier);

        Assert.Equal(ChargeOutcome.BillNotFound, result.Outcome);
    }

    [Fact]
    public async Task Controller_takes_cashier_from_jwt_and_returns_201()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var result = await ControllerChargeAsync(Request(session, Cash(250m)), cashier);

        Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
        Assert.IsType<ChargeResponse>(result.Value);
        Assert.Equal(cashier, (await FindAsync(session))!.CashierId);
    }

    [Fact]
    public async Task Controller_maps_outcomes_to_status_codes()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var insufficient = await ControllerChargeAsync(Request(session, Cash(200m)), cashier);
        await ControllerChargeAsync(Request(session, Cash(250m)), cashier);
        var duplicate = await ControllerChargeAsync(Request(session, Cash(250m)), cashier);
        var invalid = await ControllerChargeAsync(Request(session, Cash(0m)), cashier);
        var anonymous = await ControllerChargeAsync(Request(session, Cash(250m)), cashierId: null);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, insufficient.StatusCode);
        Assert.Equal(50m, ((ProblemDetails)insufficient.Value!).Extensions["deficit"]);
        Assert.Equal(StatusCodes.Status409Conflict, duplicate.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public void Keeps_wire_contract_from_pmc6()
    {
        const string json = """
            {
              "dinning_session_id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
              "propina": 15.00,
              "pagos": [{ "metodo_pago": "EFECTIVO", "monto": 250.00, "referencia_autorizacion": null }],
              "detalles_snapshot": [{ "nombre_platillo": "Tacos al Pastor", "cantidad": 2, "precio_unitario": 125.00, "importe": 250.00 }]
            }
            """;

        var request = JsonSerializer.Deserialize<ChargeRequest>(json)!;
        var response = JsonSerializer.Serialize(new ChargeResponse("TX-20260929-000001", 250m, TransactionStatus.Completed, 0m, "ok"));

        Assert.Equal((PaymentMethod.Cash, 250m), (request.Payments[0].Method, request.Payments[0].Amount));
        Assert.Equal(15m, request.Tip);
        Assert.Equal("Tacos al Pastor", request.ItemsSnapshot[0].DishName);
        Assert.Contains("\"estado\":\"COMPLETADO\"", response);
        Assert.Contains("\"cambio_entregado\":0", response);
    }

    private async Task<(Guid Session, string Cashier)> ArrangeAsync(decimal billTotal)
    {
        var session = Guid.NewGuid();
        var cashier = $"cashier-{Guid.NewGuid()}";
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        db.CashShifts.Add(new CashShift { CashierId = cashier, InitialFund = 500m });
        await db.SaveChangesAsync();

        // Same path as PMC-5: the bill arrives through DinningSessionReadyEvent into Redis.
        await scope.ServiceProvider.GetRequiredService<PendingBillService>().SaveSnapshotAsync(new DinningSessionReadyEvent(
            Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new BillItem(Guid.NewGuid(), "Tacos al Pastor", 2, billTotal / 2, billTotal)],
            new BillTotals(billTotal, 0m, billTotal)));
        return (session, cashier);
    }

    private async Task<ChargeResult> ChargeAsync(ChargeRequest request, string cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ChargeService>().ChargeAsync(request, cashierId);
    }

    private async Task<ObjectResult> ControllerChargeAsync(ChargeRequest request, string? cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var claims = cashierId is null ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, cashierId) };
        var controller = new PaymentsController(
            scope.ServiceProvider.GetRequiredService<ChargeService>(),
            scope.ServiceProvider.GetRequiredService<IValidator<ChargeRequest>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };

        var result = await controller.Charge(request, CancellationToken.None);
        return result switch
        {
            ObjectResult objectResult => objectResult,
            StatusCodeResult status => new ObjectResult(null) { StatusCode = status.StatusCode },
            _ => throw new InvalidOperationException($"Unexpected result {result.GetType()}")
        };
    }

    private async Task<PaymentTransaction?> FindAsync(Guid session)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Transactions.AsNoTracking()
            .Include(t => t.Items).Include(t => t.Payments)
            .SingleOrDefaultAsync(t => t.DinningSessionId == session);
    }

    private async Task<int> CountAsync(Guid session)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Transactions.CountAsync(t => t.DinningSessionId == session);
    }

    private async Task<int> OutboxCountAsync(Guid session)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Set<OutboxMessage>()
            .CountAsync(m => m.Body.Contains(session.ToString()));
    }

    private static ChargeRequest Request(Guid session, PaymentRequest payment) =>
        new(session, 0m, [payment], [new ItemSnapshot("Tacos al Pastor", 2, 125m, 250m)]);

    private static ChargeRequest RequestWith(Guid session, params PaymentRequest[] payments) =>
        new(session, 0m, payments, [new ItemSnapshot("Tacos al Pastor", 2, 125m, 250m)]);

    private static PaymentRequest Cash(decimal amount) => new(PaymentMethod.Cash, amount, null);

    private sealed class PaymentCompletedListener : IConsumer<PaymentCompletedEvent>
    {
        public Task Consume(ConsumeContext<PaymentCompletedEvent> context) => Task.CompletedTask;
    }

    // Simulates a SQL failure while inserting PaymentBreakdown rows (PMC-6 scenario 3).
    private sealed class FailOnBreakdownInsert : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<PaymentBreakdown>().Any(e => e.State == EntityState.Added))
            {
                throw new DbUpdateException("Simulated connection failure while inserting PaymentBreakdown.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
