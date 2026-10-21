using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public enum OpenShiftOutcome
{
    Opened,
    AlreadyOpen
}

public record OpenShiftResult(OpenShiftOutcome Outcome, OpenCashShiftResponse? Response = null, string? Error = null);

public enum CloseShiftOutcome
{
    Closed,
    NoOpenShift
}

public record CloseShiftResult(CloseShiftOutcome Outcome, CloseCashShiftResponse? Response = null, string? Error = null);

public class CashShiftService(PaymentsDbContext db)
{
    /// <summary>Opens a new cash shift for the cashier. <paramref name="cashierId"/> must come from the caller's JWT, never from the payload.</summary>
    public async Task<OpenShiftResult> OpenAsync(OpenCashShiftRequest request, string cashierId, CancellationToken ct = default)
    {
        if (await db.CashShifts.AnyAsync(s => s.CashierId == cashierId && s.Status == CashShiftStatus.Open, ct))
        {
            return AlreadyOpen();
        }

        var shift = new CashShift
        {
            CashierId = cashierId,
            InitialFund = request.InitialFund!.Value,
            Status = CashShiftStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow
        };

        db.CashShifts.Add(shift);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        // 2601/2627: duplicate key on the filtered unique index for open shifts.
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return AlreadyOpen();
        }

        return new(OpenShiftOutcome.Opened, new OpenCashShiftResponse(
            shift.Id, shift.Status, "Turno abierto exitosamente"));
    }

    /// <summary>Closes the active cash shift for the cashier and reconciles declared cash against system sales.</summary>
    public async Task<CloseShiftResult> CloseAsync(CloseCashShiftRequest request, string cashierId, CancellationToken ct = default)
    {
        await using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // UPDLOCK + HOLDLOCK serializes against ChargeService.ChargeAsync so no sale can commit into this shift while closing.
        var shift = await db.CashShifts
            .FromSqlInterpolated($"SELECT * FROM CashShifts WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE CashierId = {cashierId} AND Status = {nameof(CashShiftStatus.Open)}")
            .SingleOrDefaultAsync(ct);
        if (shift is null)
        {
            return NoOpenShift();
        }

        var transactions = await db.Transactions.AsNoTracking()
            .Include(t => t.Payments)
            .Where(t => t.CashShiftId == shift.Id && t.Status == TransactionStatus.Completed)
            .ToListAsync(ct);

        var cashSales = 0m;
        var cardTotal = 0m;
        var tipsTotal = 0m;

        foreach (var tx in transactions)
        {
            var billAmount = tx.Total - tx.Tip;
            var txCardPaid = tx.Payments.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.Amount);
            // Tips are tracked separately in TipsTotal so they are not double-counted in ExpectedCash or CardTotal.
            var netCardSale = Math.Min(txCardPaid, billAmount);
            var netCashSale = billAmount - netCardSale;

            cardTotal += netCardSale;
            cashSales += netCashSale;
            tipsTotal += tx.Tip;
        }

        var declaredCash = request.DeclaredCash!.Value;
        var expectedCash = shift.InitialFund + cashSales;
        var difference = declaredCash - expectedCash;

        shift.DeclaredCash = declaredCash;
        shift.ExpectedCash = expectedCash;
        shift.CardTotal = cardTotal;
        shift.TipsTotal = tipsTotal;
        shift.Difference = difference;
        shift.Status = difference == 0m ? CashShiftStatus.Closed : CashShiftStatus.Unbalanced;
        shift.ClosedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NoOpenShift();
        }

        await dbTransaction.CommitAsync(ct);

        var message = difference switch
        {
            0m => "Corte exacto. Turno cerrado.",
            < 0m => "Corte con faltante. Turno cerrado con descuadre.",
            > 0m => "Corte con sobrante. Turno cerrado con descuadre."
        };

        return new(CloseShiftOutcome.Closed, new CloseCashShiftResponse(
            shift.Id, shift.Status, difference, message));
    }

    private static OpenShiftResult AlreadyOpen() =>
        new(OpenShiftOutcome.AlreadyOpen, Error: "The cashier already has an open cash shift.");

    private static CloseShiftResult NoOpenShift() =>
        new(CloseShiftOutcome.NoOpenShift, Error: "The cashier has no open cash shift.");
}

