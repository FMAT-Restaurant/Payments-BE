using System.Security.Claims;
using System.Text.Json;
using FluentValidation;
using MassTransit;
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
public class CashShiftServiceTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True";

    private readonly FailOnCashShiftClose _failure = new();
    private readonly ServiceProvider _provider;

    public CashShiftServiceTests()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(_failure))
            .AddScoped<PendingBillService>()
            .AddScoped<ChargeService>()
            .AddScoped<CashShiftService>()
            .AddScoped<IValidator<ChargeRequest>, ChargeRequestValidator>()
            .AddScoped<IValidator<OpenCashShiftRequest>, OpenCashShiftRequestValidator>()
            .AddScoped<IValidator<CloseCashShiftRequest>, CloseCashShiftRequestValidator>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddEntityFrameworkOutbox<PaymentsDbContext>(o =>
                {
                    o.UseSqlServer();
                    o.UseBusOutbox();
                });
            })
            .BuildServiceProvider(true);
    }

    public async Task InitializeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await ChargeServiceTests.EnsureMigratedAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task Opening_shift_persists_open_status_for_jwt_cashier()
    {
        var cashier = NewCashier();

        var result = await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);

        Assert.Equal(OpenShiftOutcome.Opened, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.Equal(CashShiftStatus.Open, result.Response.Status);
        Assert.Equal("Turno abierto exitosamente", result.Response.Message);

        var saved = await FindShiftAsync(result.Response.CashShiftId);
        Assert.NotNull(saved);
        Assert.Equal(cashier, saved.CashierId);
        Assert.Equal(500.00m, saved.InitialFund);
        Assert.Equal(CashShiftStatus.Open, saved.Status);
        Assert.Null(saved.ClosedAt);
    }

    [Fact]
    public async Task Duplicate_open_request_is_rejected_with_conflict()
    {
        var cashier = NewCashier();
        await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);

        var duplicate = await OpenAsync(new OpenCashShiftRequest(300.00m), cashier);

        Assert.Equal(OpenShiftOutcome.AlreadyOpen, duplicate.Outcome);
        Assert.Equal(1, await OpenShiftCountAsync(cashier));
    }

    [Fact]
    public async Task Concurrent_open_requests_create_only_one_open_shift()
    {
        var cashier = NewCashier();

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => OpenAsync(new OpenCashShiftRequest(500.00m), cashier)));

        Assert.Single(results, r => r.Outcome == OpenShiftOutcome.Opened);
        Assert.All(
            results.Where(r => r.Outcome != OpenShiftOutcome.Opened),
            r => Assert.Equal(OpenShiftOutcome.AlreadyOpen, r.Outcome));
        Assert.Equal(1, await OpenShiftCountAsync(cashier));
    }

    [Fact]
    public async Task Closing_shift_with_exact_cash_match_marks_shift_as_closed()
    {
        var cashier = NewCashier();
        var opened = await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);

        // Cash sale: 2000 bill + 50 tip = 2050 paid in cash (net cash sale = 2000; 50 tip goes to waiter).
        // Card sale: 400 bill + 40 tip = 440 paid by card (net card sale = 400; 40 tip).
        // Expected cash in drawer = 500 (initial fund) + 2000 (net cash sales) = 2500.00.
        await RecordSaleAsync(cashier, billTotal: 2000.00m, tip: 50.00m, new PaymentRequest(PaymentMethod.Cash, 2050.00m, null));
        await RecordSaleAsync(cashier, billTotal: 400.00m, tip: 40.00m, new PaymentRequest(PaymentMethod.CreditCard, 440.00m, "AUTH-99"));

        var closed = await CloseAsync(new CloseCashShiftRequest(2500.00m), cashier);

        Assert.Equal(CloseShiftOutcome.Closed, closed.Outcome);
        Assert.NotNull(closed.Response);
        Assert.Equal(opened.Response!.CashShiftId, closed.Response.CashShiftId);
        Assert.Equal(CashShiftStatus.Closed, closed.Response.Status);
        Assert.Equal(0.00m, closed.Response.Difference);
        Assert.Equal("Corte exacto. Turno cerrado.", closed.Response.Message);

        var saved = await FindShiftAsync(opened.Response.CashShiftId);
        Assert.NotNull(saved);
        Assert.Equal(CashShiftStatus.Closed, saved.Status);
        Assert.Equal(2500.00m, saved.DeclaredCash);
        Assert.Equal(2500.00m, saved.ExpectedCash);
        Assert.Equal(400.00m, saved.CardTotal);
        Assert.Equal(90.00m, saved.TipsTotal);
        Assert.Equal(0.00m, saved.Difference);
        Assert.NotNull(saved.ClosedAt);
    }

    [Fact]
    public async Task Closing_shift_with_cash_shortage_marks_shift_as_unbalanced()
    {
        var cashier = NewCashier();
        var opened = await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);
        await RecordSaleAsync(cashier, billTotal: 2000.00m, tip: 0.00m, new PaymentRequest(PaymentMethod.Cash, 2000.00m, null));

        var closed = await CloseAsync(new CloseCashShiftRequest(2400.00m), cashier);

        Assert.Equal(CloseShiftOutcome.Closed, closed.Outcome);
        Assert.NotNull(closed.Response);
        Assert.Equal(CashShiftStatus.Unbalanced, closed.Response.Status);
        Assert.Equal(-100.00m, closed.Response.Difference);

        var saved = await FindShiftAsync(opened.Response!.CashShiftId);
        Assert.Equal(CashShiftStatus.Unbalanced, saved!.Status);
        Assert.Equal(-100.00m, saved.Difference);
    }

    [Fact]
    public async Task Closing_shift_with_cash_surplus_marks_shift_as_unbalanced()
    {
        var cashier = NewCashier();
        var opened = await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);
        await RecordSaleAsync(cashier, billTotal: 2000.00m, tip: 0.00m, new PaymentRequest(PaymentMethod.Cash, 2000.00m, null));

        var closed = await CloseAsync(new CloseCashShiftRequest(2650.00m), cashier);

        Assert.Equal(CloseShiftOutcome.Closed, closed.Outcome);
        Assert.Equal(CashShiftStatus.Unbalanced, closed.Response!.Status);
        Assert.Equal(150.00m, closed.Response.Difference);

        var saved = await FindShiftAsync(opened.Response!.CashShiftId);
        Assert.Equal(CashShiftStatus.Unbalanced, saved!.Status);
        Assert.Equal(150.00m, saved.Difference);
    }

    [Fact]
    public async Task Closed_shift_permanently_blocks_new_transactions_and_allows_opening_a_new_shift()
    {
        var cashier = NewCashier();
        await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);
        await CloseAsync(new CloseCashShiftRequest(500.00m), cashier);

        var secondClose = await CloseAsync(new CloseCashShiftRequest(500.00m), cashier);
        var chargeAttempt = await TryChargeOnShiftAsync(cashier, billTotal: 100.00m);
        var reopened = await OpenAsync(new OpenCashShiftRequest(300.00m), cashier);

        Assert.Equal(CloseShiftOutcome.NoOpenShift, secondClose.Outcome);
        Assert.Equal(ChargeOutcome.NoOpenShift, chargeAttempt.Outcome);
        Assert.Equal(OpenShiftOutcome.Opened, reopened.Outcome);
    }

    [Fact]
    public async Task Concurrent_close_and_charge_never_leave_sale_in_closed_shift_outside_cut()
    {
        var cashier = NewCashier();
        var opened = await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);

        var chargeTask = TryChargeOnShiftAsync(cashier, billTotal: 100.00m);
        var closeTask = CloseAsync(new CloseCashShiftRequest(500.00m), cashier);
        await Task.WhenAll(chargeTask, closeTask);

        var chargeResult = await chargeTask;
        var closeResult = await closeTask;
        var savedShift = await FindShiftAsync(opened.Response!.CashShiftId);

        Assert.Equal(CloseShiftOutcome.Closed, closeResult.Outcome);
        Assert.NotNull(savedShift);

        if (chargeResult.Outcome == ChargeOutcome.Completed)
        {
            // Sale committed before close: ExpectedCash must include the $100 sale (500 + 100 = 600).
            Assert.Equal(600.00m, savedShift.ExpectedCash);
            Assert.Equal(-100.00m, savedShift.Difference);
            Assert.Equal(CashShiftStatus.Unbalanced, savedShift.Status);
        }
        else
        {
            // Close committed first: the charge must be rejected with NoOpenShift.
            Assert.Equal(ChargeOutcome.NoOpenShift, chargeResult.Outcome);
            Assert.Equal(500.00m, savedShift.ExpectedCash);
            Assert.Equal(0.00m, savedShift.Difference);
            Assert.Equal(CashShiftStatus.Closed, savedShift.Status);
        }
    }

    [Fact]
    public async Task Empty_json_payload_is_rejected_for_open_and_close()
    {
        var cashier = NewCashier();
        var emptyOpen = JsonSerializer.Deserialize<OpenCashShiftRequest>("{}")!;
        var emptyClose = JsonSerializer.Deserialize<CloseCashShiftRequest>("{}")!;

        var openResult = await ControllerOpenAsync(emptyOpen, cashier);
        await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);
        var closeResult = await ControllerCloseAsync(emptyClose, cashier);

        Assert.Equal(StatusCodes.Status400BadRequest, openResult.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, closeResult.StatusCode);
        Assert.Equal(1, await OpenShiftCountAsync(cashier));
    }

    [Fact]
    public async Task Database_failure_on_close_rolls_back_and_keeps_shift_open()
    {
        var cashier = NewCashier();
        var opened = await OpenAsync(new OpenCashShiftRequest(500.00m), cashier);
        _failure.Enabled = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => CloseAsync(new CloseCashShiftRequest(500.00m), cashier));

        _failure.Enabled = false;
        var saved = await FindShiftAsync(opened.Response!.CashShiftId);
        Assert.Equal(CashShiftStatus.Open, saved!.Status);
        Assert.Null(saved.ClosedAt);
        Assert.Null(saved.DeclaredCash);
    }

    [Fact]
    public async Task Controller_takes_cashier_from_jwt_and_maps_outcomes_to_status_codes()
    {
        var cashier = NewCashier();

        var invalidOpen = await ControllerOpenAsync(new OpenCashShiftRequest(-10.00m), cashier);
        var unauthorizedOpen = await ControllerOpenAsync(new OpenCashShiftRequest(500.00m), cashierId: null);
        var noShiftClose = await ControllerCloseAsync(new CloseCashShiftRequest(500.00m), cashier);
        var created = await ControllerOpenAsync(new OpenCashShiftRequest(500.00m), cashier);
        var conflict = await ControllerOpenAsync(new OpenCashShiftRequest(500.00m), cashier);
        var invalidClose = await ControllerCloseAsync(new CloseCashShiftRequest(-1.00m), cashier);
        var unauthorizedClose = await ControllerCloseAsync(new CloseCashShiftRequest(500.00m), cashierId: null);
        var closed = await ControllerCloseAsync(new CloseCashShiftRequest(500.00m), cashier);

        Assert.Equal(StatusCodes.Status400BadRequest, invalidOpen.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorizedOpen.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, noShiftClose.StatusCode);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.IsType<OpenCashShiftResponse>(created.Value);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, invalidClose.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, unauthorizedClose.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, closed.StatusCode);
        Assert.IsType<CloseCashShiftResponse>(closed.Value);
    }

    [Fact]
    public void Keeps_wire_contract_from_pmc8()
    {
        const string openJson = """{ "fondo_inicial": 500.00 }""";
        const string closeJson = """{ "efectivo_declarado": 2500.00 }""";
        var shiftId = Guid.Parse("c92a5b67-8910-4f5c-b1d2-000000000001");

        var openReq = JsonSerializer.Deserialize<OpenCashShiftRequest>(openJson)!;
        var closeReq = JsonSerializer.Deserialize<CloseCashShiftRequest>(closeJson)!;
        var openRes = JsonSerializer.Serialize(new OpenCashShiftResponse(shiftId, CashShiftStatus.Open, "Turno abierto exitosamente"));
        var closeExactRes = JsonSerializer.Serialize(new CloseCashShiftResponse(shiftId, CashShiftStatus.Closed, 0.00m, "Corte exacto. Turno cerrado."));
        var closeUnbalancedRes = JsonSerializer.Serialize(new CloseCashShiftResponse(shiftId, CashShiftStatus.Unbalanced, -100.00m, "Corte con faltante. Turno cerrado con descuadre."));

        Assert.Equal(500.00m, openReq.InitialFund);
        Assert.Equal(2500.00m, closeReq.DeclaredCash);
        Assert.Contains("\"turno_caja_id\":\"c92a5b67-8910-4f5c-b1d2-000000000001\"", openRes);
        Assert.Contains("\"estado\":\"ABIERTO\"", openRes);
        Assert.Contains("\"mensaje\":\"Turno abierto exitosamente\"", openRes);
        Assert.Contains("\"estado\":\"CERRADO\"", closeExactRes);
        Assert.Contains("\"diferencia\":0.00", closeExactRes);
        Assert.Contains("\"estado\":\"DESCUADRADO\"", closeUnbalancedRes);
        Assert.Contains("\"diferencia\":-100.00", closeUnbalancedRes);
    }

    private static string NewCashier() => $"cashier-{Guid.NewGuid()}";

    private async Task<OpenShiftResult> OpenAsync(OpenCashShiftRequest request, string cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CashShiftService>().OpenAsync(request, cashierId);
    }

    private async Task<CloseShiftResult> CloseAsync(CloseCashShiftRequest request, string cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CashShiftService>().CloseAsync(request, cashierId);
    }

    private async Task RecordSaleAsync(string cashierId, decimal billTotal, decimal tip, PaymentRequest payment)
    {
        var result = await TryChargeOnShiftAsync(cashierId, billTotal, tip, payment);
        Assert.Equal(ChargeOutcome.Completed, result.Outcome);
    }

    private async Task<ChargeResult> TryChargeOnShiftAsync(
        string cashierId,
        decimal billTotal,
        decimal tip = 0m,
        PaymentRequest? payment = null)
    {
        var session = Guid.NewGuid();
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PendingBillService>().SaveSnapshotAsync(new DinningSessionReadyEvent(
            Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new BillItem(Guid.NewGuid(), "Tacos al Pastor", 1, billTotal, billTotal)],
            new BillTotals(billTotal, 0m, billTotal)));

        var actualPayment = payment ?? new PaymentRequest(PaymentMethod.Cash, billTotal + tip, null);
        var request = new ChargeRequest(
            session,
            tip,
            [actualPayment],
            [new ItemSnapshot("Tacos al Pastor", 1, billTotal, billTotal)]);

        return await scope.ServiceProvider.GetRequiredService<ChargeService>().ChargeAsync(request, cashierId);
    }

    private async Task<ObjectResult> ControllerOpenAsync(OpenCashShiftRequest request, string? cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var controller = CreateController(scope.ServiceProvider, cashierId);
        return ToObjectResult(await controller.Open(request, CancellationToken.None));
    }

    private async Task<ObjectResult> ControllerCloseAsync(CloseCashShiftRequest request, string? cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var controller = CreateController(scope.ServiceProvider, cashierId);
        return ToObjectResult(await controller.Close(request, CancellationToken.None));
    }

    private static CashShiftsController CreateController(IServiceProvider services, string? cashierId)
    {
        var claims = cashierId is null ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, cashierId) };
        return new CashShiftsController(
            services.GetRequiredService<CashShiftService>(),
            services.GetRequiredService<IValidator<OpenCashShiftRequest>>(),
            services.GetRequiredService<IValidator<CloseCashShiftRequest>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    private static ObjectResult ToObjectResult(IActionResult result) =>
        result switch
        {
            ObjectResult objectResult => objectResult,
            StatusCodeResult status => new ObjectResult(null) { StatusCode = status.StatusCode },
            _ => throw new InvalidOperationException($"Unexpected result {result.GetType()}")
        };

    private async Task<CashShift?> FindShiftAsync(Guid id)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().CashShifts.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == id);
    }

    private async Task<int> OpenShiftCountAsync(string cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().CashShifts
            .CountAsync(s => s.CashierId == cashierId && s.Status == CashShiftStatus.Open);
    }

    // Simulates a database failure / timeout while updating CashShift on close (PMC-8 scenario 5).
    private sealed class FailOnCashShiftClose : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<CashShift>().Any(e => e.State == EntityState.Modified))
            {
                throw new DbUpdateException("Simulated database timeout while closing CashShift.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
