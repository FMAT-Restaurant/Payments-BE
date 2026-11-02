using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// PMC-15 through the real HTTP pipeline (authorization, validation, UseExceptionHandler and the response stream),
// covering what DefaultHttpContext cannot prove: real status codes and a response that really has started.
public class SalesExportHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    private const string Header = "Folio,Fecha,Estado,Subtotal,Impuestos,Propina,Total,Efectivo,Tarjetas";

    // Own windows (year 2101+) and folio numbers, far from every other suite.
    private static readonly DateTimeOffset FirstWindow = new(2101, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static int _windows;
    private static long _numbers = 9_500_000_000;

    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Administrator_downloads_the_csv()
    {
        var start = NextWindow();
        var folio = (await SeedSalesAsync(start, 1)).Single();
        using var client = api.CreateAuthenticatedClient("admin-http-tests", "Administrador");

        var response = await client.GetAsync(Url(start, start.AddDays(1)));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains($"reporte_ventas_{start:yyyyMMdd}_{start:yyyyMMdd}.csv", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(new[] { Header, $"{folio},{start:yyyy-MM-dd}T00:00:00Z,COMPLETADO,10.00,0.00,0.00,10.00,10.00,0.00" }, Lines(body));
    }

    [Fact]
    public async Task Request_without_token_returns_401()
    {
        using var client = api.CreateAnonymousClient();
        var start = NextWindow();

        var response = await client.GetAsync(Url(start, start.AddDays(1)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Only "Administrador" exports the accounting report; a cashier is authenticated but not authorized.
    [Fact]
    public async Task Cashier_role_returns_403()
    {
        using var client = api.CreateAuthenticatedClient("cashier-http-tests", "Cajero");
        var start = NextWindow();

        var response = await client.GetAsync(Url(start, start.AddDays(1)));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Scenario 2 over HTTP: 400 with the English message, before any query runs.
    [Fact]
    public async Task Range_longer_than_31_days_returns_400()
    {
        using var client = api.CreateAuthenticatedClient("admin-http-tests", "Administrador");
        var start = NextWindow();

        var response = await client.GetAsync(Url(start, start.AddMonths(3)));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("The date range cannot exceed 31 days.", body);
    }

    // Scenario 3 before anything is sent: the query fails to start, so nothing reached the client and the global
    // handler still answers 500 without SQL Server internals (not a 200 with only the header and a cut connection).
    [Fact]
    public async Task Database_failure_before_sending_returns_500_without_internal_details()
    {
        var start = NextWindow();
        await SeedSalesAsync(start, 1);
        api.Failures.FailQueriesWith(start);
        using var client = api.CreateAuthenticatedClient("admin-http-tests", "Administrador");

        var response = await client.GetAsync(Url(start, start.AddDays(1)));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Simulated SQL Server timeout", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Header, body);
    }

    // Scenario 3 mid-stream: after the first 500-row chunk went out as a real 200, the database drops. The download
    // must end in an error on the client (aborted connection), never in a file that looks complete.
    [Fact]
    public async Task Database_failure_mid_stream_aborts_a_response_that_already_started()
    {
        var start = NextWindow();
        await SeedSalesAsync(start, 1_200);
        api.Failures.FailReadsAfter(start, 700);
        using var client = api.CreateAuthenticatedClient("admin-http-tests", "Administrador");

        using var response = await client.GetAsync(Url(start, start.AddDays(1)), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => response.Content.ReadAsStringAsync());
    }

    private static string Url(DateTimeOffset start, DateTimeOffset end) =>
        $"/api/v1/pagos/exportar?inicio={start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}&fin={end.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";

    private static DateTimeOffset NextWindow() => FirstWindow.AddDays(100 * Interlocked.Increment(ref _windows));

    private static string[] Lines(string body) => body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    // One sale per second from `start`, 10.00 in cash each; returns their folios in sale order.
    private async Task<List<string>> SeedSalesAsync(DateTimeOffset start, int count)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var shift = new CashShift { CashierId = $"cashier-{Guid.NewGuid()}", InitialFund = 500m };
        db.CashShifts.Add(shift);

        var sales = Enumerable.Range(0, count).Select(i =>
        {
            var number = Interlocked.Increment(ref _numbers);
            var createdAt = start.AddSeconds(i);
            return new PaymentTransaction
            {
                Number = number,
                Folio = $"TX-{createdAt:yyyyMMdd}-{number:D6}",
                DinningSessionId = Guid.NewGuid(),
                CashShiftId = shift.Id,
                CashierId = shift.CashierId,
                Subtotal = 10m,
                Total = 10m,
                Status = TransactionStatus.Completed,
                CreatedAt = createdAt,
                Payments = [new PaymentBreakdown { Method = PaymentMethod.Cash, Amount = 10m }]
            };
        }).ToList();
        db.Transactions.AddRange(sales);
        await db.SaveChangesAsync();
        return sales.Select(s => s.Folio).ToList();
    }
}
