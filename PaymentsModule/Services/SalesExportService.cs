using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class SalesExportService(PaymentsDbContext db)
{
    public const string ContentType = "text/csv; charset=utf-8";

    /// <summary>Rows written between explicit flushes, so the client receives the file in chunks.</summary>
    public const int FlushEvery = 500;

    private static readonly string[] Header =
        ["Folio", "Fecha", "Estado", "Subtotal", "Impuestos", "Propina", "Total", "Efectivo", "Tarjetas"];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Deferred query over the sales whose sale date falls in [start, end), cancelled ones included. The end is
    /// exclusive: CreatedAt keeps fractions of a second, so a month is exported as [1st 00:00, next 1st 00:00) and a
    /// sale at 23:59:59.400 on the last day can never fall between two consecutive reports.
    /// Cash and cards are summed from <see cref="PaymentBreakdown"/> in SQL. Tarjetas names CreditCard and DebitCard
    /// explicitly, so a payment method added later never lands in it by accident. Nothing is materialized:
    /// the caller pulls one row at a time from the data reader (PMC-15 AC3).
    /// </summary>
    public IAsyncEnumerable<SalesExportRow> StreamRows(DateTimeOffset start, DateTimeOffset end) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.CreatedAt >= start && t.CreatedAt < end)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Number)
            .Select(t => new SalesExportRow(
                t.Folio,
                t.CreatedAt,
                t.Status,
                t.Subtotal,
                t.Taxes,
                t.Tip,
                t.Total,
                t.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount),
                t.Payments
                    .Where(p => p.Method == PaymentMethod.CreditCard || p.Method == PaymentMethod.DebitCard)
                    .Sum(p => p.Amount)))
            .AsAsyncEnumerable();

    /// <summary>
    /// Writes the header and every row to <paramref name="output"/>, flushing every <see cref="FlushEvery"/> rows.
    /// Flushes only happen between records, so whatever reached the client is always made of complete lines.
    /// </summary>
    /// <returns>The number of data rows written.</returns>
    public static async Task<int> WriteCsvAsync(IAsyncEnumerable<SalesExportRow> rows, Stream output, CancellationToken ct = default)
    {
        // Open the query and read the first row before writing anything. If the database is down or the query fails
        // to start, the exception leaves with zero bytes sent, so the global handler can still answer 500. Writing
        // the header first would let the writers' DisposeAsync flush it while the exception unwinds, which starts
        // the response as 200 and turns the failure into an aborted download (PMC-15 scenario 3).
        await using var enumerator = rows.GetAsyncEnumerator(ct);
        var hasRow = await enumerator.MoveNextAsync();

        await using var text = new StreamWriter(output, Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true);
        await using var csv = new CsvWriter(text, new CsvConfiguration(CultureInfo.InvariantCulture));

        foreach (var column in Header)
        {
            csv.WriteField(column);
        }

        await csv.NextRecordAsync();

        var count = 0;
        while (hasRow)
        {
            var row = enumerator.Current;
            csv.WriteField(row.Folio);
            csv.WriteField(row.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            csv.WriteField(StatusLabel(row.Status));
            csv.WriteField(Money(row.Subtotal));
            csv.WriteField(Money(row.Taxes));
            csv.WriteField(Money(row.Tip));
            csv.WriteField(Money(row.Total));
            csv.WriteField(Money(row.Cash));
            csv.WriteField(Money(row.Cards));
            await csv.NextRecordAsync();

            if (++count % FlushEvery == 0)
            {
                await csv.FlushAsync();
                await text.FlushAsync(ct);
            }

            hasRow = await enumerator.MoveNextAsync();
        }

        await csv.FlushAsync();
        await text.FlushAsync(ct);
        return count;
    }

    /// <summary>
    /// Names the file after the last day actually covered: the end is exclusive, so [2026-09-01, 2026-10-01) is
    /// "reporte_ventas_20260901_20260930.csv", as in the story.
    /// </summary>
    public static string FileName(DateTimeOffset start, DateTimeOffset end)
    {
        var lastCovered = end > start ? end.AddTicks(-1) : end;
        return $"reporte_ventas_{start.UtcDateTime:yyyyMMdd}_{lastCovered.UtcDateTime:yyyyMMdd}.csv";
    }

    // Same labels as the JSON contract (JsonStringEnumMemberName on TransactionStatus).
    private static string StatusLabel(TransactionStatus status) => status switch
    {
        TransactionStatus.Completed => "COMPLETADO",
        TransactionStatus.Cancelled => "CANCELADO",
        _ => status.ToString().ToUpperInvariant()
    };

    // decimal end to end (RNF-05); a fixed two-decimal invariant format keeps the file culture independent.
    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
