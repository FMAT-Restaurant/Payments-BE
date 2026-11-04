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
    /// Writes the header and every row to <paramref name="output"/> in blocks of <see cref="FlushEvery"/> rows. Each
    /// block is built in memory and reaches <paramref name="output"/> only once it is complete, so the response only
    /// ever receives whole records: if the database drops in the middle of a block, that block is discarded.
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

        // The CSV and text writers empty their own buffers whenever they fill, in the middle of a record, and also on
        // dispose while an exception unwinds. They therefore write to `block`, never to `output`: only SendBlockAsync
        // touches the response, between records. A failure leaves the client with whole lines (nothing is written right
        // before the controller aborts the connection), and the response does not even start before the first block,
        // so a failure within the first FlushEvery rows still ends as a 500 (PMC-15 scenario 3).
        using var block = new MemoryStream();
        await using var text = new StreamWriter(block, Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true);
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
                await SendBlockAsync(csv, text, block, output, ct);
            }

            hasRow = await enumerator.MoveNextAsync();
        }

        await SendBlockAsync(csv, text, block, output, ct);
        return count;
    }

    // Moves the complete records gathered in `block` to the response in a single write, flushes it so Kestrel sends
    // the chunk, and empties the block for the next rows.
    private static async Task SendBlockAsync(CsvWriter csv, StreamWriter text, MemoryStream block, Stream output, CancellationToken ct)
    {
        await csv.FlushAsync();
        await text.FlushAsync(ct);
        await output.WriteAsync(block.GetBuffer().AsMemory(0, (int)block.Length), ct);
        await output.FlushAsync(ct);
        block.SetLength(0);
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
