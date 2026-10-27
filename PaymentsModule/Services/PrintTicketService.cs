using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class PrintTicketService(PaymentsDbContext db)
{
    /// <summary>
    /// Builds the ticket from the immutable sale history only: plain-text items and amounts persisted at charge
    /// time, no calls to other services (PMC-14 AC2). Returns null when the folio does not exist.
    /// </summary>
    // Pure projection: EF selects only the needed columns and tracks nothing. Lines and payments keep the
    // order they were charged in: ChargeService never sets their ids, so EF Core generates sequential GUIDs
    // that SQL Server sorts in insertion order, and repeated reprints are identical.
    // Kept separate from TransactionDetailService (GET /api/v1/pagos/{folio}) on purpose: same history, but this
    // is the printer's ESC/POS contract and evolves independently (see AGENTS.md, PMC-14).
    public Task<PrintTicketResponse?> GetAsync(string folio, CancellationToken ct = default) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.Folio == folio)
            .Select(t => new PrintTicketResponse(
                new PrintTicketHeader(t.Folio, t.CreatedAt, t.CashShiftId, t.DinningSessionId),
                t.Items.OrderBy(i => i.Id)
                    .Select(i => new PrintTicketLine(i.DishName, i.Quantity, i.UnitPrice, i.Amount))
                    .ToList(),
                new PrintTicketFooter(
                    t.Subtotal,
                    t.Taxes,
                    t.Tip,
                    t.Total,
                    t.Payments.OrderBy(p => p.Id)
                        .Select(p => new PrintTicketPayment(p.Method, p.Amount))
                        .ToList(),
                    t.Status)))
            .SingleOrDefaultAsync(ct);
}
