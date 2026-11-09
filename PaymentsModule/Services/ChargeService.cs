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
    AuthorizationReferenceRequired,
    InsufficientFunds,
    ElectronicOverpayment,
    CardCoversFullAmount
}

public record ChargeResult(ChargeOutcome Outcome, ChargeResponse? Response = null, string? Error = null, decimal? Deficit = null);

public class ChargeService(
    PaymentsDbContext db,
    PendingBillService bills,
    IPendingSessionIndex pendingSessions,
    IPublishEndpoint publishEndpoint,
    IChargeCalculationService calculator,
    ILogger<ChargeService> logger)
{
    /// <summary>Charges a pending bill. <paramref name="cashierId"/> must come from the caller's JWT, never from the payload.</summary>
    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, string cashierId, CancellationToken ct = default)
    {
        // Checked before the pending bill: a late DinningSessionReadyEvent may re-create it in Redis after payment.
        if (await db.Transactions.AnyAsync(t => t.DinningSessionId == request.DinningSessionId, ct))
        {
            return AlreadyPaid();
        }

        // The pending bill from Órdenes (PMC-5) is the only source of prices: the client never sends items (PMC-6).
        var bill = await bills.GetAsync(request.DinningSessionId, ct);
        if (bill is null)
        {
            if (await db.Transactions.AnyAsync(t => t.DinningSessionId == request.DinningSessionId, ct))
            {
                return AlreadyPaid();
            }

            return new(ChargeOutcome.BillNotFound, Error: "No se encontró una cuenta pendiente para esta sesión.");
        }

        var total = bill.Totals.ExpectedTotal + request.Tip;
        var calculation = calculator.Calculate(request.Payments, total);
        switch (calculation.Status)
        {
            case ChargeCalculationStatus.AuthorizationReferenceRequired:
                return new(ChargeOutcome.AuthorizationReferenceRequired, Error: "La referencia de autorización es obligatoria para pagos con tarjeta");
            case ChargeCalculationStatus.InsufficientFunds:
                return new(ChargeOutcome.InsufficientFunds, Error: "El monto de los pagos no cubre el total de la cuenta", Deficit: calculation.Deficit);
            case ChargeCalculationStatus.ElectronicOverpayment:
                return new(ChargeOutcome.ElectronicOverpayment, Error: "Los pagos con tarjeta no pueden exceder el total de la cuenta");
            case ChargeCalculationStatus.CardCoversFullAmount:
                return new(ChargeOutcome.CardCoversFullAmount, Error: "La tarjeta cubre el total; no se admite efectivo adicional");
        }

        await using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // UPDLOCK + INDEX seek locks only this cashier's open shift row, serializing against CloseAsync without blocking other cashiers.
        var shift = await db.CashShifts
            .FromSqlInterpolated($"SELECT * FROM CashShifts WITH (UPDLOCK, ROWLOCK, INDEX(IX_CashShifts_CashierId)) WHERE CashierId = {cashierId} AND Status = 'Open'")
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);
        if (shift is null)
        {
            return new(ChargeOutcome.NoOpenShift, Error: "Debe abrir caja antes de cobrar");
        }

        var transaction = new PaymentTransaction
        {
            Folio = "",
            DinningSessionId = request.DinningSessionId,
            CashShiftId = shift.Id,
            CashierId = cashierId,
            TableName = bill.TableName,
            Subtotal = bill.Totals.Subtotal,
            Taxes = bill.Totals.Taxes,
            Tip = request.Tip,
            Total = total,
            ChangeGiven = calculation.ChangeGiven,
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

        // Number: global sequence that keeps the sale order for history and export. Not composable: SQL Server rejects
        // NEXT VALUE FOR inside the subquery EF would wrap around SingleAsync.
        transaction.Number = (await db.Database
            .SqlQueryRaw<long>($"SELECT NEXT VALUE FOR {PaymentsDbContext.FolioSequence} AS Value")
            .ToListAsync(ct)).Single();
        var day = DateOnly.FromDateTime(transaction.CreatedAt.UtcDateTime);
        transaction.Folio = $"TX-{day:yyyyMMdd}-{await NextDailyNumberAsync(db, day, ct):D6}";
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

        try
        {
            await bills.RemoveAsync(request.DinningSessionId, ct);
            await pendingSessions.RemoveAsync(request.DinningSessionId);
        }
        // The sale is committed: a Redis failure must not turn it into a 500. The idempotency check keeps a stale entry harmless.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not remove session {DinningSessionId} from the pending index", request.DinningSessionId);
        }

        return new(ChargeOutcome.Completed, new ChargeResponse(
            transaction.Folio, transaction.Total, transaction.Status, calculation.ChangeGiven, "Cobro procesado exitosamente"));
    }

    /// <summary>
    /// Next folio number of <paramref name="day"/> (PMC-6 AC3): starts at 1 every day and is atomic under concurrency.
    /// Runs inside the caller's transaction, so a rolled back charge gives its number back.
    /// </summary>
    // ponytail: the counter row stays locked until the charge commits, so charges of the same day are serialized for
    // a few milliseconds; move to Redis INCR (accepting gaps) if throughput ever matters.
    public static async Task<int> NextDailyNumberAsync(PaymentsDbContext db, DateOnly day, CancellationToken ct = default) =>
        (await db.Database.SqlQuery<int>($"""
            MERGE FolioCounters WITH (HOLDLOCK) AS c
            USING (SELECT {day} AS Day) AS d ON c.Day = d.Day
            WHEN MATCHED THEN UPDATE SET LastNumber = c.LastNumber + 1
            WHEN NOT MATCHED THEN INSERT (Day, LastNumber) VALUES (d.Day, 1)
            OUTPUT inserted.LastNumber AS Value;
            """).ToListAsync(ct)).Single();

    private static ChargeResult AlreadyPaid() =>
        new(ChargeOutcome.AlreadyPaid, Error: "La transacción ya ha sido pagada previamente");
}
