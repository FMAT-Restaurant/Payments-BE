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
    /// Deferred query over the sales whose sale date falls in [start, end], cancelled ones included.
    /// Cash and cards are summed from <see cref="PaymentBreakdown"/> in SQL. Tarjetas names CreditCard and DebitCard
    /// explicitly, so a payment method added later never lands in it by accident. Nothing is materialized:
    /// the caller pulls one row at a time from the data reader (PMC-15 AC3).
    /// </summary>
    public IAsyncEnumerable<SalesExportRow> StreamRows(DateTimeOffset start, DateTimeOffset end) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.CreatedAt >= start && t.CreatedAt <= end)
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
        await using var text = new StreamWriter(output, Utf8NoBom, bufferSize: 16 * 1024, leaveOpen: true);
        await using var csv = new CsvWriter(text, new CsvConfiguration(CultureInfo.InvariantCulture));

        foreach (var column in Header)
        {
            csv.WriteField(column);
        }

        await csv.NextRecordAsync();

        var count = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
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
        }

        await csv.FlushAsync();
        await text.FlushAsync(ct);
        return count;
    }

    public static string FileName(DateTimeOffset start, DateTimeOffset end) =>
        $"reporte_ventas_{start.UtcDateTime:yyyyMMdd}_{end.UtcDateTime:yyyyMMdd}.csv";

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
