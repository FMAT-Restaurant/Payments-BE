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
    /// </summary>
    public Task<PrintTicketResponse?> GetAsync(string folio, CancellationToken ct = default)
    {
        var restaurantName = config["Restaurant:Name"] ?? "Restaurante FMAT";

        return db.Transactions.AsNoTracking()
            .Where(t => t.Folio == folio)
            .Select(t => new PrintTicketResponse(
                restaurantName,
                t.Folio,
                t.CreatedAt,
                string.IsNullOrEmpty(t.TableName) ? "Mesa 01" : t.TableName,
                t.CashierId,
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
