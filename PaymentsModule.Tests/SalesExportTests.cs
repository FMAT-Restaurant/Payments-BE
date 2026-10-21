using System.Collections;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PaymentsModule.Controllers;
using PaymentsModule.Data;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// The export scans Transactions/PaymentBreakdown; running it next to the charge tests' write transactions on the
// shared test database can make SQL Server pick it as a deadlock victim, so this collection runs on its own.
[CollectionDefinition(nameof(SalesExportTests), DisableParallelization = true)]
public class SalesExportCollection
{
}

// Runs against a real SQL Server: `docker compose up -d sqlserver` locally, a service container in CI.
// Every test works inside its own 100-day window (year 2001+), so rows written by other test classes never leak in.
[Collection(nameof(SalesExportTests))]
public class SalesExportTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True";

    private static readonly DateTimeOffset FirstWindow = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static int _windows;
    private static long _numbers = 9_000_000_000;

    private readonly DbProbe _probe = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ServiceProvider _provider;

    public SalesExportTests()
    {
        _provider = new ServiceCollection()
            .AddLogging(b => b.AddProvider(_logs))
            .AddDbContext<PaymentsDbContext>(o => o.UseSqlServer(ConnectionString).AddInterceptors(_probe))
            .AddScoped<SalesExportService>()
            .AddScoped<IValidator<SalesExportQuery>, SalesExportQueryValidator>()
            .BuildServiceProvider(true);
    }

    public async Task InitializeAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        await ChargeServiceTests.EnsureMigratedAsync(scope.ServiceProvider.GetRequiredService<PaymentsDbContext>());
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    // Scenario 1 (contents): columns, cash/card sums from PaymentBreakdown, cancelled sales included, window respected.
    [Fact]
    public async Task Export_writes_header_and_one_line_per_sale_in_the_window()
    {
        var start = NextWindow();
        var shift = await NewShiftAsync();
        var mixed = await InsertSaleAsync(shift, start.AddHours(10), 100m, 16m, 10m, TransactionStatus.Completed,
            (PaymentMethod.Cash, 26m), (PaymentMethod.CreditCard, 60m), (PaymentMethod.DebitCard, 40m));
        var cards = await InsertSaleAsync(shift, start.AddDays(1), 50m, 8m, 0m, TransactionStatus.Completed,
            (PaymentMethod.DebitCard, 58m));
        var cancelled = await InsertSaleAsync(shift, start.AddDays(2), 200m, 32m, 0m, TransactionStatus.Cancelled,
            (PaymentMethod.Cash, 232m));
        var outside = await InsertSaleAsync(shift, start.AddDays(40), 10m, 0m, 0m, TransactionStatus.Completed,
            (PaymentMethod.Cash, 10m));

        var export = await ExportAsync(start, start.AddDays(30));

        Assert.IsType<EmptyResult>(export.Result);
        Assert.Equal(StatusCodes.Status200OK, export.Http.Response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", export.Http.Response.ContentType);
        Assert.Equal(
            $"attachment; filename=\"reporte_ventas_{start:yyyyMMdd}_{start.AddDays(30):yyyyMMdd}.csv\"",
            export.Http.Response.Headers.ContentDisposition.ToString());
        Assert.Equal(
            new[]
            {
                "Folio,Fecha,Estado,Subtotal,Impuestos,Propina,Total,Efectivo,Tarjetas",
                $"{mixed},{start.AddHours(10):yyyy-MM-dd}T10:00:00Z,COMPLETADO,100.00,16.00,10.00,126.00,26.00,100.00",
                $"{cards},{start.AddDays(1):yyyy-MM-dd}T00:00:00Z,COMPLETADO,50.00,8.00,0.00,58.00,0.00,58.00",
                $"{cancelled},{start.AddDays(2):yyyy-MM-dd}T00:00:00Z,CANCELADO,200.00,32.00,0.00,232.00,232.00,0.00"
            },
            Lines(export.Body));
        Assert.DoesNotContain(outside, export.Body);
    }

    [Fact]
    public async Task Export_of_an_empty_window_returns_only_the_header()
    {
        var start = NextWindow();

        var export = await ExportAsync(start, start.AddDays(31));

        Assert.Equal(StatusCodes.Status200OK, export.Http.Response.StatusCode);
        Assert.Equal(new[] { "Folio,Fecha,Estado,Subtotal,Impuestos,Propina,Total,Efectivo,Tarjetas" }, Lines(export.Body));
    }

    // Scenario 1 (volume): 10,000 sales leave in many chunks instead of one buffered write.
    [Fact]
    public async Task Ten_thousand_sales_are_streamed_in_chunks()
    {
        var start = NextWindow();
        await BulkInsertSalesAsync(await NewShiftAsync(), start, 10_000);
        var body = new WriteCountingStream();

        var export = await ExportAsync(start, start.AddDays(30), body: body);

        Assert.Equal(StatusCodes.Status200OK, export.Http.Response.StatusCode);
        var lines = Lines(export.Body);
        Assert.Equal(10_001, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.EndsWith(",COMPLETADO,100.00,16.00,0.00,116.00,116.00,0.00", l.Split(',', 2)[1]));
        Assert.True(body.Writes >= 10_000 / SalesExportService.FlushEvery, $"Expected chunked writes, got {body.Writes}.");
    }

    // Scenario 2: rejected before touching the database.
    [Fact]
    public async Task Range_longer_than_31_days_is_rejected_without_querying_the_database()
    {
        var start = NextWindow();
        _probe.Commands = 0;

        var export = await ExportAsync(start, start.AddMonths(3));

        var badRequest = Assert.IsType<BadRequestObjectResult>(export.Result);
        var problem = Assert.IsType<ValidationProblemDetails>(badRequest.Value);
        Assert.Equal(new[] { "El rango de fechas no puede exceder los 31 días" }, problem.Errors["fin"]);
        Assert.Equal(0, _probe.Commands);
        Assert.Equal(string.Empty, export.Body);
    }

    [Fact]
    public async Task Missing_dates_are_rejected()
    {
        var export = await ExportAsync(null, null);

        var problem = Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(export.Result).Value);
        Assert.Contains("inicio", problem.Errors.Keys);
        Assert.Contains("fin", problem.Errors.Keys);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(31 * 24 * 60, true)]
    [InlineData(31 * 24 * 60 + 1, false)]
    [InlineData(-1, false)]
    public void Validator_allows_up_to_31_days_and_a_chronological_range(int minutes, bool valid)
    {
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var result = new SalesExportQueryValidator().Validate(new SalesExportQuery(start, start.AddMinutes(minutes)));

        Assert.Equal(valid, result.IsValid);
    }

    // Scenario 3: the database drops while row 551 is being read.
    [Fact]
    public async Task Database_failure_mid_stream_aborts_the_response_logs_and_keeps_complete_lines()
    {
        var start = NextWindow();
        await BulkInsertSalesAsync(await NewShiftAsync(), start, 1_000);
        _probe.FailAfterRows = 550;

        var export = await ExportAsync(start, start.AddDays(30), responseStarted: true);

        _probe.FailAfterRows = null;
        Assert.True(export.Lifetime.Aborted);
        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Error && e.Exception is SimulatedConnectionLoss && e.Message.Contains("failed mid-stream"));
        // Only whole records reached the client: header + 550 rows, each with 9 fields, ending in a line break.
        Assert.EndsWith("\r\n", export.Body);
        var lines = Lines(export.Body);
        Assert.Equal(551, lines.Length);
        Assert.All(lines, l => Assert.Equal(9, l.Split(',').Length));
    }

    // Scenario 4: an uncommitted sale is never exported (Read Committed: no dirty reads).
    [Fact]
    public async Task Uncommitted_sale_is_not_exported()
    {
        var start = NextWindow();
        var shift = await NewShiftAsync();
        var committed = await InsertSaleAsync(shift, start.AddHours(1), 100m, 16m, 0m, TransactionStatus.Completed,
            (PaymentMethod.Cash, 116m));

        await using var dirtyScope = _provider.CreateAsyncScope();
        var dirtyDb = dirtyScope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await using var dirty = await dirtyDb.Database.BeginTransactionAsync();
        var uncommitted = NewSale(shift, start.AddHours(2), 300m, 48m, 0m, TransactionStatus.Completed, (PaymentMethod.Cash, 348m));
        dirtyDb.Transactions.Add(uncommitted);
        await dirtyDb.SaveChangesAsync();

        var exporting = ExportAsync(start, start.AddDays(30));
        // The cashier's transaction rolls back while the export is running (or waiting on its lock).
        await Task.Delay(300);
        await dirty.RollbackAsync();
        var export = await exporting;

        Assert.Equal(StatusCodes.Status200OK, export.Http.Response.StatusCode);
        Assert.Contains(committed, export.Body);
        Assert.DoesNotContain(uncommitted.Folio, export.Body);
    }

    [Fact]
    public void Endpoint_is_restricted_to_accountants_and_administrators()
    {
        var attribute = typeof(SalesExportController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("Contador,Administrador", attribute?.Roles);
    }

    private async Task<Export> ExportAsync(
        DateTimeOffset? start, DateTimeOffset? end, bool responseStarted = false, MemoryStream? body = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        var http = new DefaultHttpContext();
        var lifetime = new TrackingLifetimeFeature();
        http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        if (responseStarted)
        {
            http.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        }

        body ??= new MemoryStream();
        http.Response.Body = body;
        var controller = new SalesExportController(
            scope.ServiceProvider.GetRequiredService<SalesExportService>(),
            scope.ServiceProvider.GetRequiredService<IValidator<SalesExportQuery>>(),
            scope.ServiceProvider.GetRequiredService<ILogger<SalesExportController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = await controller.Export(start, end, CancellationToken.None);
        return new Export(result, http, lifetime, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private static DateTimeOffset NextWindow() => FirstWindow.AddDays(100 * Interlocked.Increment(ref _windows));

    private static string[] Lines(string body) =>
        body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private async Task<Guid> NewShiftAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var shift = new CashShift { CashierId = $"cashier-{Guid.NewGuid()}", InitialFund = 500m };
        db.CashShifts.Add(shift);
        await db.SaveChangesAsync();
        return shift.Id;
    }

    private async Task<string> InsertSaleAsync(
        Guid shift, DateTimeOffset createdAt, decimal subtotal, decimal taxes, decimal tip, TransactionStatus status,
        params (PaymentMethod Method, decimal Amount)[] payments)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var sale = NewSale(shift, createdAt, subtotal, taxes, tip, status, payments);
        db.Transactions.Add(sale);
        await db.SaveChangesAsync();
        return sale.Folio;
    }

    private static PaymentTransaction NewSale(
        Guid shift, DateTimeOffset createdAt, decimal subtotal, decimal taxes, decimal tip, TransactionStatus status,
        params (PaymentMethod Method, decimal Amount)[] payments)
    {
        var number = Interlocked.Increment(ref _numbers);
        return new PaymentTransaction
        {
            Number = number,
            Folio = $"TX-{createdAt:yyyyMMdd}-{number:D6}",
            DinningSessionId = Guid.NewGuid(),
            CashShiftId = shift,
            CashierId = "cashier-export",
            Subtotal = subtotal,
            Taxes = taxes,
            Tip = tip,
            Total = subtotal + taxes + tip,
            Status = status,
            CreatedAt = createdAt,
            Payments = payments.Select(p => new PaymentBreakdown { Method = p.Method, Amount = p.Amount }).ToList()
        };
    }

    // Set-based insert: one sale per second from `start`, each paid in cash.
    private async Task BulkInsertSalesAsync(Guid shift, DateTimeOffset start, int count)
    {
        var firstNumber = Interlocked.Add(ref _numbers, count) - count;
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Transactions (Id, Number, Folio, DinningSessionId, CashShiftId, CashierId, Subtotal, Taxes, Tip, Total, Status, CreatedAt)
            SELECT NEWID(), {firstNumber} + n, CONCAT('TX-BULK-', {firstNumber} + n), NEWID(), {shift}, 'cashier-export',
                   100.00, 16.00, 0.00, 116.00, 'Completed', DATEADD(SECOND, n, {start})
            FROM (SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbers;

            INSERT INTO PaymentBreakdown (Id, TransactionId, Method, Amount)
            SELECT NEWID(), Id, 'Cash', Total FROM Transactions WHERE CashShiftId = {shift};
            """);
    }

    private sealed record Export(IActionResult Result, DefaultHttpContext Http, TrackingLifetimeFeature Lifetime, string Body);

    // Counts commands and, when FailAfterRows is set, makes the next data reader fail after that many rows.
    private sealed class DbProbe : DbCommandInterceptor
    {
        public int Commands { get; set; }
        public int? FailAfterRows { get; set; }

        public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
        {
            Commands++;
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FailAfterRows is { } rows ? new FailingReader(result, rows) : result);
    }

    private sealed class SimulatedConnectionLoss() : DbException("Simulated SQL Server disconnection mid-stream.");

    private sealed class FailingReader(DbDataReader inner, int failAfter) : DbDataReader
    {
        private int _rows;

        public override bool Read() => Advance(inner.Read());
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken) => Advance(await inner.ReadAsync(cancellationToken));

        private bool Advance(bool hasRow)
        {
            if (hasRow && ++_rows > failAfter)
            {
                throw new SimulatedConnectionLoss();
            }

            return hasRow;
        }

        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override void Close() => inner.Close();
        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // DefaultHttpContext never reports a started response; production does once the first chunk is flushed.
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class TrackingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class WriteCountingStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            base.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Writes++;
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<(LogLevel, string, Exception?)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }
}
