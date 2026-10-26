using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentValidation;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentsModule.Controllers;
using PaymentsModule.Data;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// PMC-13 Gherkin scenarios against the real SQL Server, same pattern as ChargeServiceTests.
// Every test uses its own dinning session and cashier, because test classes run in parallel on the same database.
public class ChangeCalculationTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True";

    // Shared by the test provider and the TestServer host, so the pending bill seeded here is visible to both.
    private readonly IDistributedCache _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly TimeoutOnBreakdownInsert _timeout = new();
    private readonly ServiceProvider _provider;

    public ChangeCalculationTests()
    {
        var services = new ServiceCollection().AddLogging();
        RegisterApplication(services);
        _provider = services.BuildServiceProvider(true);
    }

    private ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    public async Task InitializeAsync()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            await ChargeServiceTests.EnsureMigratedAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
        }

        await Harness.Start();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private void RegisterApplication(IServiceCollection services)
    {
        services.AddSingleton(_cache);
        services.AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(_timeout));
        services.AddScoped<PendingBillService>();
        services.AddScoped<ChargeService>();
        services.AddSingleton<IChargeCalculationService, ChargeCalculationService>();
        services.AddScoped<IValidator<ChargeRequest>, ChargeRequestValidator>();
        services.AddMassTransitTestHarness(x =>
            x.AddEntityFrameworkOutbox<PaymentsDbContext>(o =>
            {
                o.UseSqlServer();
                o.UseBusOutbox();
            }));
    }

    [Fact]
    public async Task Scenario1_cash_above_the_total_returns_change_and_stores_only_the_amount_due()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);

        var result = await ControllerChargeAsync(Request(session, Cash(100m)), cashier);

        Assert.Equal(StatusCodes.Status201Created, result.StatusCode);
        var response = Assert.IsType<ChargeResponse>(result.Value);
        Assert.Equal((80m, TransactionStatus.Completed, 20m), (response.Total, response.Status, response.ChangeGiven));
        Assert.Equal("Cobro procesado exitosamente", response.Message);

        var saved = await FindAsync(session);
        Assert.NotNull(saved);
        Assert.Equal(80m, saved.Total);
        var cash = Assert.Single(saved.Payments);
        Assert.Equal((PaymentMethod.Cash, 80m), (cash.Method, cash.Amount));
        Assert.Equal(saved.Total, saved.Payments.Sum(p => p.Amount));
    }

    [Fact]
    public void Scenario1_response_carries_cambio_entregado_on_the_wire()
    {
        var json = JsonSerializer.Serialize(new ChargeResponse("TX-20260929-000005", 80m, TransactionStatus.Completed, 20.00m, "Cobro procesado exitosamente"));

        using var document = JsonDocument.Parse(json);
        Assert.Equal(20.00m, document.RootElement.GetProperty("cambio_entregado").GetDecimal());
        Assert.Equal(80m, document.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal("COMPLETADO", document.RootElement.GetProperty("estado").GetString());
    }

    [Theory]
    [InlineData(PaymentMethod.DebitCard)]
    [InlineData(PaymentMethod.CreditCard)]
    public async Task Scenario2_card_above_the_total_is_rejected_without_touching_the_database(PaymentMethod card)
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 150m);

        var result = await ControllerChargeAsync(Request(session, new PaymentRequest(card, 160m, "AUTH-1")), cashier);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Equal("Electronic payments cannot exceed the bill total", Assert.IsType<ProblemDetails>(result.Value).Detail);
        Assert.Null(await FindAsync(session));
        Assert.Equal(0, await OutboxCountAsync(session));
    }

    [Fact]
    public async Task Scenario2_mixed_payment_whose_cards_exceed_the_total_is_rejected()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 150m);

        var result = await ChargeAsync(Request(session, Card(PaymentMethod.CreditCard, 100m), Card(PaymentMethod.DebitCard, 60m), Cash(20m)), cashier);

        Assert.Equal(ChargeOutcome.ElectronicOverpayment, result.Outcome);
        Assert.Null(await FindAsync(session));
    }

    [Fact]
    public async Task Mixed_payment_with_cash_excess_is_accepted_and_cash_absorbs_it()
    {
        // PMC-13-P1: card 50 + cash 100 over 120.
        var (session, cashier) = await ArrangeAsync(billTotal: 120m);

        var result = await ChargeAsync(Request(session, Card(PaymentMethod.CreditCard, 50m), Cash(100m)), cashier);

        Assert.Equal((ChargeOutcome.Completed, 30m), (result.Outcome, result.Response!.ChangeGiven));
        var saved = await FindAsync(session);
        Assert.Equal(120m, saved!.Total);
        Assert.Equal(50m, saved.Payments.Single(p => p.Method == PaymentMethod.CreditCard).Amount);
        Assert.Equal(70m, saved.Payments.Single(p => p.Method == PaymentMethod.Cash).Amount);
    }

    [Fact]
    public async Task Card_covering_the_bill_returns_all_the_cash_as_change_and_stores_no_cash()
    {
        // PMC-13-P6.
        var (session, cashier) = await ArrangeAsync(billTotal: 100m);

        var result = await ChargeAsync(Request(session, Card(PaymentMethod.DebitCard, 100m), Cash(50m)), cashier);

        Assert.Equal(50m, result.Response!.ChangeGiven);
        var payment = Assert.Single((await FindAsync(session))!.Payments);
        Assert.Equal((PaymentMethod.DebitCard, 100m), (payment.Method, payment.Amount));
    }

    [Fact]
    public async Task Tip_is_part_of_the_amount_due_when_computing_change()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);

        var result = await ChargeAsync(Request(session, Cash(100m)) with { Tip = 12.50m }, cashier);

        Assert.Equal((92.50m, 7.50m), (result.Response!.Total, result.Response.ChangeGiven));
        Assert.Equal(92.50m, Assert.Single((await FindAsync(session))!.Payments).Amount);
    }

    [Fact]
    public async Task Scenario3_database_timeout_rolls_back_everything()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);
        _timeout.FailFor(session);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => ChargeAsync(Request(session, Cash(100m)), cashier));

        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Null(await FindAsync(session));
        Assert.Equal(0, await OutboxCountAsync(session));
    }

    [Fact]
    public async Task Scenario3_database_timeout_answers_a_generic_500_through_the_pipeline()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);
        _timeout.FailFor(session);

        var (status, body) = await PostThroughPipelineAsync(Request(session, Cash(100m)), cashier);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(500, problem.RootElement.GetProperty("status").GetInt32());
        // PMC-13-P9: standard ProblemDetails without internals.
        Assert.DoesNotContain("Simulated", body);
        Assert.DoesNotContain("PaymentBreakdown", body);
        Assert.Null(await FindAsync(session));
    }

    [Fact]
    public async Task Scenario4_resent_payload_is_rejected_with_409_without_new_rows()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);
        var request = Request(session, Cash(100m));
        var first = await ControllerChargeAsync(request, cashier);

        var retry = await ControllerChargeAsync(request, cashier);

        Assert.Equal(20m, Assert.IsType<ChargeResponse>(first.Value).ChangeGiven);
        Assert.Equal(StatusCodes.Status409Conflict, retry.StatusCode);
        Assert.Equal("The transaction has already been paid", Assert.IsType<ProblemDetails>(retry.Value).Detail);
        Assert.Equal(1, await CountAsync(session));
        Assert.Single((await FindAsync(session))!.Payments);
    }

    [Fact]
    public async Task Scenario4_concurrent_resends_charge_and_compute_change_only_once()
    {
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ChargeAsync(Request(session, Cash(100m)), cashier)));

        var completed = Assert.Single(results, r => r.Outcome == ChargeOutcome.Completed);
        Assert.Equal(20m, completed.Response!.ChangeGiven);
        Assert.All(results.Where(r => r != completed), r => Assert.Equal(ChargeOutcome.AlreadyPaid, r.Outcome));
        Assert.Equal(1, await CountAsync(session));
    }

    [Fact]
    public async Task Scenario4_a_cancelled_sale_also_blocks_a_new_charge()
    {
        // PMC-13-P8: any previous transaction of the session, even CANCELADO, answers 409.
        var (session, cashier) = await ArrangeAsync(billTotal: 80m);
        await ChargeAsync(Request(session, Cash(80m)), cashier);
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Transactions
                .Where(t => t.DinningSessionId == session)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TransactionStatus.Cancelled));
        }

        var retry = await ChargeAsync(Request(session, Cash(100m)), cashier);

        Assert.Equal(ChargeOutcome.AlreadyPaid, retry.Outcome);
        Assert.Equal(1, await CountAsync(session));
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
            [new BillItem(Guid.NewGuid(), "Hamburguesa Sencilla", 1, billTotal, billTotal)],
            new BillTotals(billTotal, 0m, billTotal)));
        return (session, cashier);
    }

    private async Task<ChargeResult> ChargeAsync(ChargeRequest request, string cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ChargeService>().ChargeAsync(request, cashierId);
    }

    private async Task<ObjectResult> ControllerChargeAsync(ChargeRequest request, string cashierId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var controller = new PaymentsController(
            scope.ServiceProvider.GetRequiredService<ChargeService>(),
            scope.ServiceProvider.GetRequiredService<IValidator<ChargeRequest>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cashierId)], "Test"))
                }
            }
        };

        return Assert.IsAssignableFrom<ObjectResult>(await controller.Charge(request, CancellationToken.None));
    }

    // Calling the controller directly skips UseExceptionHandler, so the 500 is checked through a real TestServer pipeline.
    private async Task<(HttpStatusCode Status, string Body)> PostThroughPipelineAsync(ChargeRequest request, string cashierId)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddControllers().AddApplicationPart(typeof(PaymentsController).Assembly);
                    services.AddProblemDetails();
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    RegisterApplication(services);
                });
                web.Configure(app =>
                {
                    // Same order as Program.cs.
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();

        using var client = host.GetTestClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/pagos/cobrar") { Content = JsonContent.Create(request) };
        message.Headers.Add("X-Test-User", cashierId);
        using var response = await client.SendAsync(message);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<PaymentTransaction?> FindAsync(Guid session)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Transactions.AsNoTracking()
            .Include(t => t.Payments)
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

    private static ChargeRequest Request(Guid session, params PaymentRequest[] payments) =>
        new(session, 0m, payments, [new ItemSnapshot("Hamburguesa Sencilla", 1, 80m, 80m)]);

    private static PaymentRequest Cash(decimal amount) => new(PaymentMethod.Cash, amount, null);

    private static PaymentRequest Card(PaymentMethod method, decimal amount) => new(method, amount, "AUTH-1");

    // Simulates a SQL Server timeout while inserting the capped PaymentBreakdown (PMC-13 scenario 3).
    // Scoped to one session so the other tests of this class, running against the same interceptor, are not affected.
    private sealed class TimeoutOnBreakdownInsert : SaveChangesInterceptor
    {
        private readonly HashSet<Guid> _sessions = [];

        public void FailFor(Guid session)
        {
            lock (_sessions)
            {
                _sessions.Add(session);
            }
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var sessions = eventData.Context!.ChangeTracker.Entries<PaymentTransaction>()
                .Where(e => e.State == EntityState.Added && e.Entity.Payments.Count > 0)
                .Select(e => e.Entity.DinningSessionId);
            bool fail;
            lock (_sessions)
            {
                fail = sessions.Any(_sessions.Contains);
            }

            if (fail)
            {
                throw new DbUpdateException("Simulated SQL Server timeout while inserting PaymentBreakdown.", new TimeoutException("Execution Timeout Expired."));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
