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
    private readonly InMemoryPendingSessionIndex _pending = new();
    private readonly ServiceProvider _provider;

    public ChargeServiceTests()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddSingleton<IPendingSessionIndex>(_pending)
            .AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(_failure))
            .AddScoped<PendingBillService>()
            .AddScoped<ChargeService>()
            .AddSingleton<IChargeCalculationService, ChargeCalculationService>()
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
        Assert.Equal("Mesa 04", saved.TableName);
        Assert.Equal(0m, saved.ChangeGiven);
        // PMC-6: a charged session leaves the pending index.
        Assert.False(_pending.Contains(session));
        var item = Assert.Single(saved.Items);
        Assert.Equal("Tacos al Pastor", item.DishName);
        var payment = Assert.Single(saved.Payments);
        Assert.Equal((PaymentMethod.Cash, 250m), (payment.Method, payment.Amount));

        var consumed = false;
        for (var i = 0; i < 50 && !consumed; i++)
        {
            consumed = await Harness.Consumed.Any<PaymentCompletedEvent>(m =>
                m.Context.Message.DinningSessionId == session && m.Context.Message.Folio == saved.Folio);
            if (!consumed) await Task.Delay(200);
        }
        Assert.True(consumed);
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
    public async Task Card_covering_full_amount_with_cash_is_rejected()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 100m);

        var result = await ChargeAsync(Request(session, Card(PaymentMethod.CreditCard, 100m), Cash(50m)), cashier);

        Assert.Equal(ChargeOutcome.CardCoversFullAmount, result.Outcome);
        Assert.Null(await FindAsync(session));
    }

    // PMC-7 scenario 3: a card payment without its terminal reference is rejected before any write.
    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Card_without_authorization_reference_is_rejected(string? reference)
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 500m);

        var result = await ChargeAsync(Request(session, new PaymentRequest(PaymentMethod.DebitCard, 500m, reference)), cashier);

        Assert.Equal(ChargeOutcome.AuthorizationReferenceRequired, result.Outcome);
        Assert.Equal("La referencia de autorización es obligatoria para pagos con tarjeta", result.Error);
        Assert.Null(await FindAsync(session));
    }

    // PMC-6 AC3: the folio number restarts every day and never repeats under concurrency (scenario 4).
    [Fact]
    public async Task Daily_folio_number_is_unique_under_concurrency()
    {
        // A far-future day no charge uses; a counter left by a past run only shifts the start.
        var day = new DateOnly(2100, 1, 1).AddDays(Random.Shared.Next(0, 300 * 365));
        int before;
        await using (var scope = _provider.CreateAsyncScope())
        {
            before = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().FolioCounters
                .Where(c => c.Day == day).Select(c => c.LastNumber).SingleOrDefaultAsync();
        }

        var numbers = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            var number = await ChargeService.NextDailyNumberAsync(db, day);
            await tx.CommitAsync();
            return number;
        }));

        Assert.Equal(Enumerable.Range(before + 1, 20), numbers.Order());
    }

    [Fact]
    public async Task Rolled_back_charge_gives_its_folio_number_back()
    {
        var day = new DateOnly(2100, 1, 1).AddDays(Random.Shared.Next(0, 300 * 365));
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

        int first;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            first = await ChargeService.NextDailyNumberAsync(db, day);
            await tx.RollbackAsync();
        }

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.Equal(first, await ChargeService.NextDailyNumberAsync(db, day));
            await tx.CommitAsync();
        }
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
    public async Task Controller_maps_outcomes_to_error_response_codes()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 250m);

        var insufficient = await ControllerChargeAsync(Request(session, Cash(200m)), cashier);
        await ControllerChargeAsync(Request(session, Cash(250m)), cashier);
        var duplicate = await ControllerChargeAsync(Request(session, Cash(250m)), cashier);
        var invalid = await ControllerChargeAsync(Request(session, Cash(0m)), cashier);
        var anonymous = await ControllerChargeAsync(Request(session, Cash(250m)), cashierId: null);

        // 422 INSUFFICIENT_FUNDS with deficit
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, insufficient.StatusCode);
        var insufficientError = Assert.IsType<ErrorResponse>(insufficient.Value);
        Assert.Equal("INSUFFICIENT_FUNDS", insufficientError.Code);
        Assert.Equal("El monto de los pagos no cubre el total de la cuenta", insufficientError.Message);
        Assert.Equal(50m, insufficientError.Deficit);

        // 409 TRANSACTION_ALREADY_PAID
        Assert.Equal(StatusCodes.Status409Conflict, duplicate.StatusCode);
        var duplicateError = Assert.IsType<ErrorResponse>(duplicate.Value);
        Assert.Equal("TRANSACTION_ALREADY_PAID", duplicateError.Code);
        Assert.Equal("La transacción ya ha sido pagada previamente", duplicateError.Message);

        // 400 and 401 remain unchanged
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public void Keeps_wire_contract_from_pmc7_and_pmc13()
    {
        const string json = """
            {
              "dinning_session_id": "a1b2c3d4-e5f6-7890-1234-56789abcdef0",
              "tip": 15.00,
              "payments": [
                { "method": "EFECTIVO", "amount": 200.00, "authorization_reference": null },
                { "method": "TARJETA_CREDITO", "amount": 300.00, "authorization_reference": "AUTH-987654" }
              ]
            }
            """;

        var request = JsonSerializer.Deserialize<ChargeRequest>(json)!;
        var response = JsonSerializer.Serialize(new ChargeResponse("TX-20261107-000005", 80m, TransactionStatus.Completed, 20m, "ok"));

        Assert.Equal(15m, request.Tip);
        Assert.Equal((PaymentMethod.Cash, 200m, (string?)null), (request.Payments[0].Method, request.Payments[0].Amount, request.Payments[0].AuthorizationReference));
        Assert.Equal((PaymentMethod.CreditCard, "AUTH-987654"), (request.Payments[1].Method, request.Payments[1].AuthorizationReference));
        Assert.Equal("""{"folio":"TX-20261107-000005","total":80,"estado":"COMPLETADO","change_given":20,"mensaje":"ok"}""", response);
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
            new BillTotals(billTotal, 0m, billTotal),
            TableName: "Mesa 04"));
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

    private static ChargeRequest Request(Guid session, params PaymentRequest[] payments) => new(session, 0m, payments);

    private static PaymentRequest Cash(decimal amount) => new(PaymentMethod.Cash, amount, null);

    private static PaymentRequest Card(PaymentMethod method, decimal amount) => new(method, amount, "AUTH-1");

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
