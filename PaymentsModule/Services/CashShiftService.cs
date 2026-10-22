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

public enum ShiftSummaryOutcome
{
    Found,
    NoOpenShift
}

public record ShiftSummaryResult(ShiftSummaryOutcome Outcome, CashShiftSummaryResponse? Response = null, string? Error = null);

public class CashShiftService(PaymentsDbContext db)
{
    /// <summary>Calculates the real-time income summary for the cashier's active open shift without modifying state.</summary>
    public async Task<ShiftSummaryResult> GetSummaryAsync(string cashierId, CancellationToken ct = default)
    {
        await using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var shift = await db.CashShifts.AsNoTracking()
            .SingleOrDefaultAsync(s => s.CashierId == cashierId && s.Status == CashShiftStatus.Open, ct);
        if (shift is null)
        {
            return new(ShiftSummaryOutcome.NoOpenShift, Error: "The cashier has no open cash shift.");
        }

        var (cashSales, cardTotal, _) = await CalculateShiftTotalsAsync(shift.Id, ct);
        var expectedCash = shift.InitialFund + cashSales;

        return new(ShiftSummaryOutcome.Found, new CashShiftSummaryResponse(
            shift.Id,
            shift.Status,
            shift.InitialFund,
            new CashShiftIncomeBreakdown(cashSales, cardTotal),
            expectedCash,
            DateTimeOffset.UtcNow));
    }

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

        // UPDLOCK + INDEX seek locks only this cashier's open shift row, serializing against ChargeAsync without blocking other cashiers.
        var shift = await db.CashShifts
            .FromSqlInterpolated($"SELECT * FROM CashShifts WITH (UPDLOCK, ROWLOCK, INDEX(IX_CashShifts_CashierId)) WHERE CashierId = {cashierId} AND Status = 'Open'")
            .SingleOrDefaultAsync(ct);
        if (shift is null)
        {
            return NoOpenShift();
        }

        var (cashSales, cardTotal, tipsTotal) = await CalculateShiftTotalsAsync(shift.Id, ct);

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

    private async Task<(decimal CashSales, decimal CardTotal, decimal TipsTotal)> CalculateShiftTotalsAsync(Guid shiftId, CancellationToken ct)
    {
        var breakdownByMethod = await db.Transactions.AsNoTracking()
            .Where(t => t.CashShiftId == shiftId && t.Status == TransactionStatus.Completed)
            .SelectMany(t => t.Payments)
            .GroupBy(p => p.Method)
            .Select(g => new
            {
                Method = g.Key,
                Total = g.Sum(p => p.Amount)
            })
            .ToListAsync(ct);

        var tipTotals = await db.Transactions.AsNoTracking()
            .Where(t => t.CashShiftId == shiftId && t.Status == TransactionStatus.Completed && t.Tip > 0m)
            .Select(t => new
            {
                t.Tip,
                CashPaid = t.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount)
            })
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TipsTotal = g.Sum(x => x.Tip),
                CashTips = g.Sum(x => x.CashPaid >= x.Tip ? x.Tip : x.CashPaid)
            })
            .SingleOrDefaultAsync(ct);

        var rawCash = breakdownByMethod.Where(x => x.Method == PaymentMethod.Cash).Sum(x => x.Total);
        var rawCard = breakdownByMethod.Where(x => x.Method != PaymentMethod.Cash).Sum(x => x.Total);

        var tipsTotal = tipTotals?.TipsTotal ?? 0m;
        var cashTips = tipTotals?.CashTips ?? 0m;
        var cardTips = tipsTotal - cashTips;

        return (rawCash - cashTips, rawCard - cardTips, tipsTotal);
    }

    private static OpenShiftResult AlreadyOpen() =>
        new(OpenShiftOutcome.AlreadyOpen, Error: "The cashier already has an open cash shift.");

    private static CloseShiftResult NoOpenShift() =>
        new(CloseShiftOutcome.NoOpenShift, Error: "The cashier has no open cash shift.");
}

