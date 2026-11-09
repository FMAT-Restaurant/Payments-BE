using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class SalesHistoryService(PaymentsDbContext db)
{
    /// <summary>Paged sales history. Filters are applied only when present, and everything runs in the database (PMC-10).</summary>
    public async Task<PagedResponse<SalesHistoryItem>> GetAsync(SalesHistoryFilter filter, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", ct);

        // Only completed transactions; no external service calls or joins (AC1).
        var query = db.Transactions
            .AsNoTracking()
            .Where(t => t.Status == TransactionStatus.Completed);

        if (filter.StartDate is { } start)
        {
            query = query.Where(t => t.CreatedAt >= start);
        }

        // Semi-open interval [start_date, end_date): end_date is exclusive (AC2).
        if (filter.EndDate is { } end)
        {
            query = query.Where(t => t.CreatedAt < end);
        }

        var totalCount = await query.CountAsync(ct);
        var skip = (int)Math.Min((long)(filter.PageIndex - 1) * filter.PageSize, int.MaxValue);

        // Selective projection: only the listed columns are read, from the plain-text snapshot.
        var data = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(filter.PageSize)
            .Select(t => new SalesHistoryItem(
                t.Folio,
                t.CashShiftId,
                t.Total,
                t.Status,
                t.CreatedAt,
                t.Items
                    .OrderBy(i => i.Id)
                    .Select(i => new SalesHistoryLine(i.DishName, i.Quantity, i.UnitPrice))
                    .ToList(),
                t.Payments
                    .OrderBy(p => p.Id)
                    .Select(p => new SalesHistoryPaymentBreakdown(p.Method, p.Amount))
                    .ToList()))
            .ToListAsync(ct);

        var totalPages = (totalCount + filter.PageSize - 1) / filter.PageSize;
        return new PagedResponse<SalesHistoryItem>(filter.PageIndex, filter.PageSize, totalCount, totalPages, data);
    }
}
