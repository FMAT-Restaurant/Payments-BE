using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class TransactionDetailService(PaymentsDbContext db)
{
    /// <summary>Returns the stored ticket for a folio, or <c>null</c> when it does not exist.</summary>
    public async Task<TransactionDetailResponse?> GetByFolioAsync(string folio, CancellationToken ct = default)
    {
        // Read-only: no change tracking. One query loads the header and both child collections (no N+1).
        // Everything comes from the plain-text snapshot saved at charge time; the product catalog is never consulted.
        var transaction = await db.Transactions
            .AsNoTracking()
            .Include(t => t.Items)
            .Include(t => t.Payments)
            .FirstOrDefaultAsync(t => t.Folio == folio, ct);

        if (transaction is null)
        {
            return null;
        }

        return new TransactionDetailResponse(
            transaction.Folio,
            transaction.CashShiftId,
            transaction.DinningSessionId,
            transaction.Total,
            transaction.Status,
            transaction.Items
                .Select(i => new TransactionItemResponse(i.DishName, i.Quantity, i.UnitPrice, i.Amount))
                .ToList(),
            transaction.Payments
                .Select(p => new PaymentBreakdownResponse(p.Method, p.Amount, p.AuthorizationReference))
                .ToList());
    }
}
