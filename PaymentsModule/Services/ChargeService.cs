using System.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public enum ChargeOutcome
{
    Completed,
    AlreadyPaid,
    BillNotFound,
    NoOpenShift,
    InsufficientFunds,
    ElectronicOverpayment
}

public record ChargeResult(ChargeOutcome Outcome, ChargeResponse? Response = null, string? Error = null, decimal? Deficit = null);

public class ChargeService(PaymentsDbContext db, PendingBillService bills, IPublishEndpoint publishEndpoint)
{
    /// <summary>Charges a pending bill. <paramref name="cashierId"/> must come from the caller's JWT, never from the payload.</summary>
    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, string cashierId, CancellationToken ct = default)
    {
        // Checked before the pending bill: a late DinningSessionReadyEvent may re-create it in Redis after payment.
        if (await db.Transactions.AnyAsync(t => t.DinningSessionId == request.DinningSessionId, ct))
        {
            return AlreadyPaid();
        }

        // The pending bill from Órdenes (PMC-5) is the source of truth for prices; the client snapshot is not trusted.
        var bill = await bills.GetAsync(request.DinningSessionId, ct);
        if (bill is null)
        {
            return new(ChargeOutcome.BillNotFound, Error: "No pending bill was found for this dinning session.");
        }

        var shift = await db.CashShifts.AsNoTracking()
            .SingleOrDefaultAsync(s => s.CashierId == cashierId && s.Status == CashShiftStatus.Open, ct);
        if (shift is null)
        {
            return new(ChargeOutcome.NoOpenShift, Error: "The cashier has no open cash shift.");
        }

        var total = bill.Totals.ExpectedTotal + request.Tip;
        var received = request.Payments.Sum(p => p.Amount);
        if (received < total)
        {
            var deficit = total - received;
            return new(ChargeOutcome.InsufficientFunds, Error: $"Insufficient funds: {deficit:0.00} missing to cover {total:0.00}.", Deficit: deficit);
        }

        // Card payments cannot produce change; only cash may exceed the amount due.
        if (request.Payments.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.Amount) > total)
        {
            return new(ChargeOutcome.ElectronicOverpayment, Error: "Electronic payments cannot exceed the bill total.");
        }

        var transaction = new PaymentTransaction
        {
            Folio = "",
            DinningSessionId = request.DinningSessionId,
            CashShiftId = shift.Id,
            CashierId = cashierId,
            Subtotal = bill.Totals.Subtotal,
            Taxes = bill.Totals.Taxes,
            Tip = request.Tip,
            Total = total,
            Status = TransactionStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            Items = bill.Items.Select(i => new TransactionItem
            {
                DishName = i.Name,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                Amount = i.Amount
            }).ToList(),
            Payments = ApplyPayments(request.Payments, total)
        };

        await using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Not composable: SQL Server rejects NEXT VALUE FOR inside the subquery EF would wrap around SingleAsync.
        transaction.Number = (await db.Database
            .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR {PaymentsDbContext.FolioSequence} AS Value")
            .ToListAsync(ct)).Single();
        // ponytail: global sequence and UTC date, so the number does not restart daily; switch to a per-day counter if the business asks for it.
        transaction.Folio = $"TX-{transaction.CreatedAt:yyyyMMdd}-{transaction.Number:D6}";
        db.Transactions.Add(transaction);

        // Stored in the outbox within this DB transaction; MassTransit delivers it only after COMMIT.
        await publishEndpoint.Publish(new PaymentCompletedEvent(
            Guid.NewGuid(), transaction.DinningSessionId, transaction.Folio, transaction.Total, transaction.CreatedAt), ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        // 2601/2627: duplicate key on a unique index / constraint.
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // A concurrent retry for the same session committed first.
            return AlreadyPaid();
        }

        await dbTransaction.CommitAsync(ct);

        var change = received - total;
        return new(ChargeOutcome.Completed, new ChargeResponse(
            transaction.Folio, transaction.Total, transaction.Status, change, "Cobro procesado exitosamente"));
    }

    /// <summary>Caps each payment to what is still owed, so cash change is never stored as income.</summary>
    private static List<PaymentBreakdown> ApplyPayments(IEnumerable<PaymentRequest> payments, decimal total)
    {
        var remaining = total;
        var breakdown = new List<PaymentBreakdown>();
        // Electronic payments first: they can never be capped, so cash absorbs any excess.
        foreach (var payment in payments.OrderBy(p => p.Method == PaymentMethod.Cash))
        {
            var applied = Math.Min(payment.Amount, remaining);
            if (applied == 0m)
            {
                continue;
            }

            remaining -= applied;
            breakdown.Add(new PaymentBreakdown
            {
                Method = payment.Method,
                Amount = applied,
                AuthorizationReference = payment.AuthorizationReference
            });
        }

        return breakdown;
    }

    private static ChargeResult AlreadyPaid() =>
        new(ChargeOutcome.AlreadyPaid, Error: "The bill for this dinning session has already been paid.");
}
