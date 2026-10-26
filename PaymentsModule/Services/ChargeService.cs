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

public class ChargeService(PaymentsDbContext db, PendingBillService bills, IPublishEndpoint publishEndpoint, IChargeCalculationService calculator)
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

        var total = bill.Totals.ExpectedTotal + request.Tip;
        var calculation = calculator.Calculate(request.Payments, total);
        switch (calculation.Status)
        {
            case ChargeCalculationStatus.InsufficientFunds:
                // Exact text of PMC-7 scenario 2; the missing amount travels in the "deficit" extension of the ProblemDetails.
                return new(ChargeOutcome.InsufficientFunds, Error: "Fondos insuficientes para cubrir el total", Deficit: calculation.Deficit);
            case ChargeCalculationStatus.ElectronicOverpayment:
                // Rejected before any write; exact text confirmed by the PO (PMC-13-P4).
                return new(ChargeOutcome.ElectronicOverpayment, Error: "Los pagos electrónicos no pueden exceder el total de la cuenta");
        }

        await using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // UPDLOCK + INDEX seek locks only this cashier's open shift row, serializing against CloseAsync without blocking other cashiers.
        var shift = await db.CashShifts
            .FromSqlInterpolated($"SELECT * FROM CashShifts WITH (UPDLOCK, ROWLOCK, INDEX(IX_CashShifts_CashierId)) WHERE CashierId = {cashierId} AND Status = 'Open'")
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);
        if (shift is null)
        {
            return new(ChargeOutcome.NoOpenShift, Error: "The cashier has no open cash shift.");
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
            Payments = calculation.Breakdown.ToList()
        };

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

        // Change is only returned in the response, never persisted (PMC-13-P7).
        return new(ChargeOutcome.Completed, new ChargeResponse(
            transaction.Folio, transaction.Total, transaction.Status, calculation.ChangeGiven, "Cobro procesado exitosamente"));
    }

    private static ChargeResult AlreadyPaid() =>
        new(ChargeOutcome.AlreadyPaid, Error: "La transacción ya ha sido pagada previamente");
}
