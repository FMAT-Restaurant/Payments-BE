using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentsModule.Controllers;
using PaymentsModule.Data;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// Runs against a real SQL Server: `docker compose up -d sqlserver` locally, a service container in CI.
// The bank gateway is a scripted HTTP handler behind the real retry/timeout pipeline.
public class SaleCancellationTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True";

    // Same pipeline as production (3 retries, exponential backoff, 30 s budget) with a tiny base delay to keep the tests fast.
    private static readonly BankGatewayOptions GatewayOptions = new()
    {
        BaseUrl = "http://bank.test/",
        UseMock = false,
        RetryCount = 3,
        RetryDelay = TimeSpan.FromMilliseconds(1),
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly FailOnCancellationInsert _failure = new();
    private readonly ScriptedBankHandler _bank = new();
    private readonly ServiceProvider _provider;

    public SaleCancellationTests()
    {
        var services = new ServiceCollection().AddLogging();
        RegisterApplication(services);
        _provider = services.BuildServiceProvider(true);
    }

    public async Task InitializeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await ChargeServiceTests.EnsureMigratedAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private void RegisterApplication(IServiceCollection services)
    {
        services.AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(_failure));
        services.AddScoped<SaleCancellationService>();
        services.AddScoped<IValidator<CancelSaleRequest>, CancelSaleRequestValidator>();
        services.AddHttpClient<IBankGatewayClient, BankGatewayClient>(http => http.BaseAddress = new Uri(GatewayOptions.BaseUrl))
            .ConfigurePrimaryHttpMessageHandler(() => _bank)
            .AddBankGatewayResilience(GatewayOptions);
    }

    // ---- Scenario 2: happy path, cash only ----

    [Fact]
    public async Task Cash_only_sale_is_cancelled_without_contacting_the_gateway()
    {
        var sale = await SeedSaleAsync(0m, Cash(250m));

        var result = await CancelAsync(sale.Folio, "Customer reported an error in the bill", "manager-1");

        Assert.Equal(CancelSaleOutcome.Cancelled, result.Outcome);
        Assert.Equal((sale.Folio, TransactionStatus.Cancelled, SaleCancellationService.SuccessMessage),
            (result.Response!.Folio, result.Response.Status, result.Response.Message));
        Assert.Empty(_bank.Calls);

        var saved = await FindSaleAsync(sale.Folio);
        Assert.Equal(TransactionStatus.Cancelled, saved.Status);
        var cancellation = Assert.Single(await FindCancellationsAsync(sale.Id));
        Assert.Equal(("manager-1", "Customer reported an error in the bill"), (cancellation.AuthorizerId, cancellation.Reason));
        Assert.Equal(result.Response.CancelledAt, cancellation.CancelledAt);
        Assert.Null(cancellation.GatewayReference);
        Assert.Equal(0m, cancellation.RefundedAmount);
    }

    // ---- AC3: nothing is deleted ----

    [Fact]
    public async Task Cancelling_keeps_the_ticket_with_its_items_and_payments()
    {
        var sale = await SeedSaleAsync(0m, Cash(100m), Card(150m, "AUTH-1"));

        await CancelAsync(sale.Folio);

        var saved = await FindSaleAsync(sale.Folio);
        Assert.Equal(250m, saved.Total);
        Assert.Single(saved.Items);
        Assert.Equal(2, saved.Payments.Count);

        await using var scope = _provider.CreateAsyncScope();
        var detail = await new TransactionDetailService(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>()).GetByFolioAsync(sale.Folio);
        Assert.Equal(TransactionStatus.Cancelled, detail!.Status);
        Assert.Equal(2, detail.Payments.Count);
    }

    // ---- AC4: refunds through the gateway ----

    [Theory]
    [InlineData(PaymentMethod.CreditCard)]
    [InlineData(PaymentMethod.DebitCard)]
    public async Task Card_sale_is_refunded_through_the_gateway(PaymentMethod method)
    {
        var sale = await SeedSaleAsync(0m, (method, 300m, "AUTH-300"));

        var result = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.Cancelled, result.Outcome);
        var call = Assert.Single(_bank.Calls);
        Assert.Equal((sale.Folio, "AUTH-300", 300m), (call.Body.Folio, call.Body.AuthorizationReference, call.Body.Amount));
        var cancellation = Assert.Single(await FindCancellationsAsync(sale.Id));
        Assert.Equal(300m, cancellation.RefundedAmount);
        Assert.StartsWith("REFUND-", cancellation.GatewayReference);
    }

    [Fact]
    public async Task Mixed_sale_refunds_only_the_card_payments_one_call_each()
    {
        // Card amounts already include any tip charged to the card; cash never goes through the gateway.
        var sale = await SeedSaleAsync(20m, Cash(100m), Card(200m, "AUTH-A"), (PaymentMethod.DebitCard, 70m, "AUTH-B"));

        var result = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.Cancelled, result.Outcome);
        var refunds = _bank.Calls.Select(c => (c.Body.AuthorizationReference, c.Body.Amount)).OrderBy(c => c.AuthorizationReference).ToList();
        Assert.Equal(2, refunds.Count);
        Assert.Equal(("AUTH-A", 200m), refunds[0]);
        Assert.Equal(("AUTH-B", 70m), refunds[1]);
        Assert.Equal(2, _bank.Calls.Select(c => c.IdempotencyKey).Distinct().Count());
        Assert.Equal(270m, Assert.Single(await FindCancellationsAsync(sale.Id)).RefundedAmount);
    }

    // ---- Scenario 3: already cancelled / double click ----

    [Fact]
    public async Task Already_cancelled_sale_is_denied_without_a_second_record_or_refund()
    {
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"));
        var first = await CancelAsync(sale.Folio);

        var second = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.Cancelled, first.Outcome);
        Assert.Equal(CancelSaleOutcome.AlreadyCancelled, second.Outcome);
        Assert.Equal("La transacción ya se encuentra cancelada", second.Error);
        Assert.Single(_bank.Calls);
        Assert.Single(await FindCancellationsAsync(sale.Id));
    }

    [Fact]
    public async Task Concurrent_cancellations_cancel_once_and_refund_once()
    {
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CancelAsync(sale.Folio)));

        Assert.Single(results, r => r.Outcome == CancelSaleOutcome.Cancelled);
        Assert.All(results.Where(r => r.Outcome != CancelSaleOutcome.Cancelled), r => Assert.Equal(CancelSaleOutcome.AlreadyCancelled, r.Outcome));
        Assert.Single(_bank.Calls);
        Assert.Single(await FindCancellationsAsync(sale.Id));
    }

    // ---- Scenario 4 / AC5: gateway failure and rollback ----

    [Fact]
    public async Task Gateway_outage_exhausts_the_retries_and_leaves_the_sale_completed()
    {
        _bank.Default = HttpStatusCode.ServiceUnavailable;
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"));

        var result = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.GatewayFailure, result.Outcome);
        Assert.Equal(1 + GatewayOptions.RetryCount, _bank.Calls.Count);
        Assert.Equal(TransactionStatus.Completed, (await FindSaleAsync(sale.Folio)).Status);
        Assert.Empty(await FindCancellationsAsync(sale.Id));
    }

    [Fact]
    public async Task Transient_gateway_failure_is_retried_with_the_same_idempotency_key()
    {
        _bank.Script(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"));

        var result = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.Cancelled, result.Outcome);
        Assert.Equal(3, _bank.Calls.Count);
        Assert.Single(_bank.Calls.Select(c => c.IdempotencyKey).Distinct());
        Assert.Single(await FindCancellationsAsync(sale.Id));
    }

    [Fact]
    public async Task Gateway_rejection_is_not_retried_and_aborts_the_cancellation()
    {
        _bank.Default = HttpStatusCode.BadRequest;
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"));

        var result = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.GatewayFailure, result.Outcome);
        Assert.Single(_bank.Calls);
        Assert.Equal(TransactionStatus.Completed, (await FindSaleAsync(sale.Folio)).Status);
        Assert.Empty(await FindCancellationsAsync(sale.Id));
    }

    [Fact]
    public async Task Failure_on_a_later_card_refund_aborts_everything_locally()
    {
        _bank.Script(HttpStatusCode.OK);
        _bank.Default = HttpStatusCode.ServiceUnavailable;
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"), (PaymentMethod.DebitCard, 50m, "AUTH-2"));

        var result = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.GatewayFailure, result.Outcome);
        Assert.Equal(1 + 1 + GatewayOptions.RetryCount, _bank.Calls.Count);
        Assert.Equal(TransactionStatus.Completed, (await FindSaleAsync(sale.Folio)).Status);
        Assert.Empty(await FindCancellationsAsync(sale.Id));
    }

    [Fact]
    public async Task Database_failure_after_the_refund_rolls_back_and_a_retry_reuses_the_idempotency_key()
    {
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"));
        _failure.Enabled = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => CancelAsync(sale.Folio));

        _failure.Enabled = false;
        Assert.Equal(TransactionStatus.Completed, (await FindSaleAsync(sale.Folio)).Status);
        Assert.Empty(await FindCancellationsAsync(sale.Id));
        // The gateway already refunded and cannot be rolled back: the stable key lets a retry avoid refunding twice.
        Assert.Single(_bank.Calls);

        var retry = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.Cancelled, retry.Outcome);
        Assert.Equal(2, _bank.Calls.Count);
        Assert.Single(_bank.Calls.Select(c => c.IdempotencyKey).Distinct());
    }

    // ---- Card payments without a reference (PMC-12-P6) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Card_payment_without_reference_blocks_the_cancellation_before_any_refund(string? reference)
    {
        var sale = await SeedSaleAsync(0m, Card(100m, "AUTH-1"), (PaymentMethod.DebitCard, 50m, reference));

        var result = await CancelAsync(sale.Folio);

        Assert.Equal(CancelSaleOutcome.MissingAuthorizationReference, result.Outcome);
        Assert.Empty(_bank.Calls);
        Assert.Equal(TransactionStatus.Completed, (await FindSaleAsync(sale.Folio)).Status);
        Assert.Empty(await FindCancellationsAsync(sale.Id));
    }

    // ---- AC2: unknown folio, exact match ----

    [Fact]
    public async Task Unknown_folio_is_not_found()
    {
        var result = await CancelAsync("TX-00000000-999999");

        Assert.Equal(CancelSaleOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Folio_is_matched_exactly_and_case_sensitively()
    {
        var sale = await SeedSaleAsync(0m, Cash(50m));

        // SQL Server's default collation would match both of these.
        var lowerCase = await CancelAsync(sale.Folio.ToLowerInvariant());
        var trailingSpace = await CancelAsync(sale.Folio + " ");

        Assert.Equal(CancelSaleOutcome.NotFound, lowerCase.Outcome);
        Assert.Equal(CancelSaleOutcome.NotFound, trailingSpace.Outcome);
        Assert.Equal(TransactionStatus.Completed, (await FindSaleAsync(sale.Folio)).Status);
        Assert.Empty(await FindCancellationsAsync(sale.Id));
    }

    // ---- Reason validation (PMC-12-P8) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reason_is_required(string? reason)
    {
        Assert.False(new CancelSaleRequestValidator().Validate(new CancelSaleRequest(reason)).IsValid);
    }

    [Fact]
    public void Reason_is_limited_to_500_characters_after_trimming()
    {
        var validator = new CancelSaleRequestValidator();

        Assert.False(validator.Validate(new CancelSaleRequest(new string('x', 501))).IsValid);
        Assert.True(validator.Validate(new CancelSaleRequest(new string('x', 500))).IsValid);
        Assert.True(validator.Validate(new CancelSaleRequest("  " + new string('x', 500) + "  ")).IsValid);
        Assert.True(validator.Validate(new CancelSaleRequest("x")).IsValid);
    }

    // ---- Controller: identity and status codes ----

    [Fact]
    public async Task Controller_takes_the_authorizer_from_the_jwt_and_trims_the_reason()
    {
        var sale = await SeedSaleAsync(0m, Cash(80m));

        var result = await ControllerCancelAsync(sale.Folio, new CancelSaleRequest("  Wrong table charged  "), "manager-77");

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.IsType<CancelSaleResponse>(result.Value);
        var cancellation = Assert.Single(await FindCancellationsAsync(sale.Id));
        Assert.Equal(("manager-77", "Wrong table charged"), (cancellation.AuthorizerId, cancellation.Reason));
    }

    [Fact]
    public async Task Controller_maps_outcomes_to_status_codes()
    {
        var cash = await SeedSaleAsync(0m, Cash(80m));
        var card = await SeedSaleAsync(0m, Card(100m, "AUTH-1"));
        var noReference = await SeedSaleAsync(0m, Card(100m, null));
        var request = new CancelSaleRequest("Customer reported an error in the bill");

        var invalid = await ControllerCancelAsync(cash.Folio, new CancelSaleRequest("   "), "manager-1");
        var anonymous = await ControllerCancelAsync(cash.Folio, request, null);
        var missing = await ControllerCancelAsync("TX-00000000-999999", request, "manager-1");
        var unprocessable = await ControllerCancelAsync(noReference.Folio, request, "manager-1");
        _bank.Default = HttpStatusCode.ServiceUnavailable;
        var badGateway = await ControllerCancelAsync(card.Folio, request, "manager-1");
        _bank.Default = HttpStatusCode.OK;
        var ok = await ControllerCancelAsync(card.Folio, request, "manager-1");
        var conflict = await ControllerCancelAsync(card.Folio, request, "manager-1");

        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unprocessable.StatusCode);
        Assert.Equal(StatusCodes.Status502BadGateway, badGateway.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal("La transacción ya se encuentra cancelada", ((ProblemDetails)conflict.Value!).Detail);
    }

    // ---- AC1 / Scenario 1: RBAC through the real ASP.NET Core pipeline ----

    [Theory]
    [InlineData("Gerente", StatusCodes.Status200OK)]
    [InlineData("Administrador", StatusCodes.Status200OK)]
    [InlineData("Cajero", StatusCodes.Status403Forbidden)]
    [InlineData("gerente", StatusCodes.Status403Forbidden)]
    [InlineData("Admin", StatusCodes.Status403Forbidden)]
    public async Task Pipeline_authorizes_only_gerente_and_administrador(string role, int expectedStatus)
    {
        var sale = await SeedSaleAsync(0m, Cash(60m));

        var (status, _) = await PostThroughPipelineAsync(sale.Folio, user: "user-1", role: role);

        Assert.Equal(expectedStatus, (int)status);
        var cancellations = await FindCancellationsAsync(sale.Id);
        Assert.Equal(expectedStatus == StatusCodes.Status200OK ? 1 : 0, cancellations.Count);
        Assert.Equal(expectedStatus == StatusCodes.Status200OK ? TransactionStatus.Cancelled : TransactionStatus.Completed, (await FindSaleAsync(sale.Folio)).Status);
    }

    [Fact]
    public async Task Pipeline_rejects_a_request_without_a_token_with_401()
    {
        var sale = await SeedSaleAsync(0m, Cash(60m));

        var (status, _) = await PostThroughPipelineAsync(sale.Folio, user: null, role: null);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Empty(await FindCancellationsAsync(sale.Id));
    }

    [Fact]
    public async Task Pipeline_answers_the_pmc12_wire_contract()
    {
        var sale = await SeedSaleAsync(0m, Cash(60m));

        var (status, body) = await PostThroughPipelineAsync(sale.Folio, user: "manager-1", role: "Gerente");

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(sale.Folio, root.GetProperty("folio").GetString());
        Assert.Equal("CANCELADO", root.GetProperty("estado").GetString());
        Assert.Equal(SaleCancellationService.SuccessMessage, root.GetProperty("mensaje").GetString());
        var cancellation = Assert.Single(await FindCancellationsAsync(sale.Id));
        Assert.Equal(cancellation.CancelledAt, root.GetProperty("fecha_cancelacion").GetDateTimeOffset());
    }

    [Fact]
    public void Keeps_the_wire_contract_from_pmc12()
    {
        var request = JsonSerializer.Deserialize<CancelSaleRequest>("""{ "motivo": "Customer reported an error" }""")!;
        var response = JsonSerializer.Serialize(new CancelSaleResponse(
            "TX-20261023-000001", TransactionStatus.Cancelled, new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero), "ok"));

        Assert.Equal("Customer reported an error", request.Reason);
        Assert.Contains("\"folio\":\"TX-20261023-000001\"", response);
        Assert.Contains("\"estado\":\"CANCELADO\"", response);
        Assert.Contains("\"fecha_cancelacion\":", response);
        Assert.Contains("\"mensaje\":\"ok\"", response);
    }

    // ---- Regression: a cancelled sale no longer counts in the cash shift close (PMC-8) ----

    [Fact]
    public async Task Cancelled_sale_no_longer_counts_in_the_cash_shift_close()
    {
        var sale = await SeedSaleAsync(0m, Cash(100m));
        await CancelAsync(sale.Folio);

        await using var scope = _provider.CreateAsyncScope();
        var shifts = new CashShiftService(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
        // The shift opened with 500.00 and its only sale was cancelled, so the drawer should hold exactly the initial fund.
        var close = await shifts.CloseAsync(new CloseCashShiftRequest(500m), sale.CashierId);

        Assert.Equal(CloseShiftOutcome.Closed, close.Outcome);
        Assert.Equal(0m, close.Response!.Difference);
    }

    // ---- Helpers ----

    private static (PaymentMethod Method, decimal Amount, string? Reference) Cash(decimal amount) =>
        (PaymentMethod.Cash, amount, null);

    private static (PaymentMethod Method, decimal Amount, string? Reference) Card(decimal amount, string? reference) =>
        (PaymentMethod.CreditCard, amount, reference);

    // Every sale gets its own cashier, shift and folio, so test classes can run in parallel on the same database.
    private async Task<PaymentTransaction> SeedSaleAsync(decimal tip, params (PaymentMethod Method, decimal Amount, string? Reference)[] payments)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var shift = new CashShift { CashierId = $"cashier-{Guid.NewGuid()}", InitialFund = 500m };
        db.CashShifts.Add(shift);

        // Same global sequence as ChargeService, so numbers never collide with the folios other test classes create here.
        var number = (await db.Database
            .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR {PaymentsDbContext.FolioSequence} AS Value")
            .ToListAsync()).Single();
        var total = payments.Sum(p => p.Amount);
        var createdAt = DateTimeOffset.UtcNow;
        var sale = new PaymentTransaction
        {
            Number = number,
            Folio = $"TX-{createdAt:yyyyMMdd}-{number:D6}",
            DinningSessionId = Guid.NewGuid(),
            CashShiftId = shift.Id,
            CashierId = shift.CashierId,
            Subtotal = total - tip,
            Tip = tip,
            Total = total,
            Status = TransactionStatus.Completed,
            CreatedAt = createdAt,
            Items = [new TransactionItem { DishName = "Ribeye", Quantity = 1, UnitPrice = total - tip, Amount = total - tip }],
            Payments = payments
                .Select(p => new PaymentBreakdown { Method = p.Method, Amount = p.Amount, AuthorizationReference = p.Reference })
                .ToList()
        };
        db.Transactions.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }

    private async Task<CancelSaleResult> CancelAsync(string folio, string reason = "Customer reported an error in the bill", string authorizerId = "manager-1")
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SaleCancellationService>().CancelAsync(folio, reason, authorizerId);
    }

    private async Task<ObjectResult> ControllerCancelAsync(string folio, CancelSaleRequest request, string? authorizerId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var claims = authorizerId is null ? [] : new[] { new Claim(ClaimTypes.NameIdentifier, authorizerId) };
        var controller = new SaleCancellationController(
            scope.ServiceProvider.GetRequiredService<SaleCancellationService>(),
            scope.ServiceProvider.GetRequiredService<IValidator<CancelSaleRequest>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };

        var result = await controller.Cancel(folio, request, CancellationToken.None);
        return result switch
        {
            ObjectResult objectResult => objectResult,
            StatusCodeResult statusResult => new ObjectResult(null) { StatusCode = statusResult.StatusCode },
            _ => throw new InvalidOperationException($"Unexpected result {result.GetType()}")
        };
    }

    // Direct controller calls skip the authorization filter, so RBAC is checked through a real TestServer pipeline.
    private async Task<(HttpStatusCode Status, string Body)> PostThroughPipelineAsync(string folio, string? user, string? role)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(SaleCancellationController).Assembly);
                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddAuthorization();
                    RegisterApplication(services);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();

        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/pagos/{folio}/cancelar")
        {
            Content = JsonContent.Create(new { motivo = "Customer reported an error in the bill" })
        };
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }

        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }

        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<PaymentTransaction> FindSaleAsync(string folio)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Transactions.AsNoTracking()
            .Include(t => t.Items).Include(t => t.Payments)
            .SingleAsync(t => t.Folio == folio);
    }

    private async Task<List<SaleCancellation>> FindCancellationsAsync(Guid transactionId)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().SaleCancellations.AsNoTracking()
            .Where(c => c.TransactionId == transactionId)
            .ToListAsync();
    }

    private sealed record RecordedRefund(BankRefundHttpRequest Body, string IdempotencyKey);

    // Plays the bank gateway: records every call and answers the scripted statuses first, then the default one.
    private sealed class ScriptedBankHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpStatusCode> _script = new();

        public HttpStatusCode Default { get; set; } = HttpStatusCode.OK;

        public ConcurrentQueue<RecordedRefund> Calls { get; } = new();

        public void Script(params HttpStatusCode[] statuses)
        {
            foreach (var status in statuses)
            {
                _script.Enqueue(status);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read as text: the retry pipeline resends the same request, and a stream read would close the cached content stream.
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonSerializer.Deserialize<BankRefundHttpRequest>(json, JsonSerializerOptions.Web);
            Calls.Enqueue(new RecordedRefund(body!, request.Headers.GetValues("Idempotency-Key").Single()));

            var response = new HttpResponseMessage(_script.TryDequeue(out var next) ? next : Default);
            if (response.IsSuccessStatusCode)
            {
                response.Content = JsonContent.Create(new BankRefundHttpResponse($"REFUND-{Guid.NewGuid():N}"));
            }

            return response;
        }
    }

    // Simulates a database failure while inserting the cancellation record.
    private sealed class FailOnCancellationInsert : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<SaleCancellation>().Any(e => e.State == EntityState.Added))
            {
                throw new DbUpdateException("Simulated database failure while inserting SaleCancellation.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // Authenticates from test headers; no header means no identity (401), a wrong role means 403.
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Role", out var role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }
}
