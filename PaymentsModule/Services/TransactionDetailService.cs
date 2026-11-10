using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class TransactionDetailService(PaymentsDbContext db)
{
    /// <summary>
    /// Returns the stored transaction detail for a folio in a single query projection, or <c>null</c> when it does not exist (PMC-11).
    /// </summary>
    public async Task<TransactionDetailResponse?> GetByFolioAsync(string folio, CancellationToken ct = default)
    {
        // Read-only: no change tracking. A single database query projects header, items, and breakdowns (no N+1).
        // Everything comes from the plain-text snapshot saved at charge time; the product catalog is never consulted (AC1, AC2).
        return await db.Transactions
            .AsNoTracking()
            .Where(t => t.Folio == folio)
            .Select(t => new TransactionDetailResponse(
                t.Folio,
                t.CashShiftId,
                t.DinningSessionId,
                t.Status,
                t.CreatedAt,
                t.Subtotal,
                t.Taxes,
                t.Tip,
                t.Total,
                t.ChangeGiven,
                t.Items
                    .OrderBy(i => i.Id)
                    .Select(i => new TransactionItemResponse(i.DishName, i.Quantity, i.UnitPrice, i.Amount))
                    .ToList(),
                t.Payments
                    .OrderBy(p => p.Id)
                    .Select(p => new PaymentBreakdownResponse(p.Method, p.Amount, p.AuthorizationReference))
                    .ToList()))
            .SingleOrDefaultAsync(ct);
    }
}
