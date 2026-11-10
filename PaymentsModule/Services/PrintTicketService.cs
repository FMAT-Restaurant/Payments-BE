using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class PrintTicketService(PaymentsDbContext db, IConfiguration config)
{
    /// <summary>
    /// Builds the ticket from the immutable sale history only: plain-text items and amounts persisted at charge
    /// time, without consulting other microservices (PMC-14 AC1, AC2). Returns null when the folio does not exist.
    /// Lines and payments keep the order they were charged in: sequential GUIDs sort in insertion order.
    /// </summary>
    public Task<PrintTicketResponse?> GetAsync(string folio, CancellationToken ct = default)
    {
        var restaurantName = config["Restaurant:Name"] ?? "Restaurante FMAT";

        return (from t in db.Transactions.AsNoTracking()
                where t.Folio == folio
                join s in db.CashShifts.AsNoTracking() on t.CashShiftId equals s.Id into shifts
                from s in shifts.DefaultIfEmpty()
                select new PrintTicketResponse(
                    restaurantName,
                    t.Folio,
                    t.CreatedAt,
                    t.TableName ?? string.Empty,
                    s != null && s.CashierName != null && s.CashierName != "" ? s.CashierName : t.CashierId,
                    t.Items.OrderBy(i => i.Id)
                        .Select(i => new PrintTicketLine(i.DishName, i.Quantity, i.UnitPrice, i.Amount))
                        .ToList(),
                    t.Subtotal,
                    t.Taxes,
                    t.Tip,
                    t.Total,
                    t.Payments.OrderBy(p => p.Id)
                        .Select(p => new PrintTicketPayment(p.Method, p.Amount, p.AuthorizationReference))
                        .ToList(),
                    t.ChangeGiven))
            .SingleOrDefaultAsync(ct);
    }
}
