using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class SalesHistoryService(PaymentsDbContext db)
{
    /// <summary>Paged sales history. Filters are applied only when present, and everything runs in the database.</summary>
    public async Task<PagedResponse<SalesHistoryItem>> GetAsync(SalesHistoryFilter filter, CancellationToken ct = default)
    {
        var query = db.Transactions.AsNoTracking().AsQueryable();

        if (filter.From is { } start)
        {
            query = query.Where(t => t.CreatedAt >= start);
        }

        if (filter.To is { } end)
        {
            query = query.Where(t => t.CreatedAt <= end);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(t => t.Status == status);
        }

        if (filter.Method is { } method)
        {
            query = query.Where(t => t.Payments.Any(p => p.Method == method));
        }

        var totalCount = await query.CountAsync(ct);
        var skip = (int)Math.Min((long)(filter.PageIndex - 1) * filter.PageSize, int.MaxValue);

        // Selective projection: only the listed columns are read, from the plain-text snapshot (no catalog access).
        var data = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(filter.PageSize)
            .Select(t => new SalesHistoryItem(
                t.Folio,
                t.Total,
                t.Status,
                t.CreatedAt,
                t.Items.Select(i => new SalesHistoryLine(i.DishName, i.Quantity, i.UnitPrice)).ToList()))
            .ToListAsync(ct);

        var totalPages = (totalCount + filter.PageSize - 1) / filter.PageSize;
        return new PagedResponse<SalesHistoryItem>(filter.PageIndex, filter.PageSize, totalCount, totalPages, data);
    }
}
