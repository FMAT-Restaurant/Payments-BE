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

/// <summary>Shift totals shared by the summary (PMC-9), the close (PMC-8) and later PMC-16/PMC-32. Amounts in <see cref="decimal"/>.</summary>
/// <param name="CashTotal">Cash kept in the drawer: cash received minus change given (the capped cash breakdowns).</param>
/// <param name="RetainedTips">Tip share paid by card, proportional to each card amount (PMC-9 AC3, pending PO confirmation).</param>
public record ShiftTotals(decimal CashTotal, decimal CardTotal, int CardTransactionCount, decimal RetainedTips, decimal TipsTotal);

public class CashShiftService(PaymentsDbContext db)
{
    /// <summary>Calculates the real-time summary of the cashier's open shift without modifying state.</summary>
    public async Task<ShiftSummaryResult> GetSummaryAsync(string cashierId, CancellationToken ct = default)
    {
        var shift = await db.CashShifts.AsNoTracking()
            .SingleOrDefaultAsync(s => s.CashierId == cashierId && s.Status == CashShiftStatus.Open, ct);
        if (shift is null)
        {
            return new(ShiftSummaryOutcome.NoOpenShift, Error: "No hay un turno de caja abierto para este usuario");
        }

        var totals = await CalculateShiftTotalsAsync(shift.Id, ct);

        return new(ShiftSummaryOutcome.Found, new CashShiftSummaryResponse(
            shift.Id,
            shift.CashierName,
            shift.Status,
            shift.OpenedAt,
            shift.InitialFund,
            ExpectedCash(shift, totals),
            totals.CardTotal,
            totals.CardTransactionCount,
            totals.RetainedTips,
            DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Opens a new cash shift. <paramref name="cashierId"/> and <paramref name="cashierName"/> must come from the caller's JWT,
    /// never from the payload.
    /// </summary>
    public async Task<OpenShiftResult> OpenAsync(OpenCashShiftRequest request, string cashierId, string? cashierName, CancellationToken ct = default)
    {
        if (await db.CashShifts.AnyAsync(s => s.CashierId == cashierId && s.Status == CashShiftStatus.Open, ct))
        {
            return AlreadyOpen();
        }

        var shift = new CashShift
        {
            CashierId = cashierId,
            CashierName = cashierName,
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

        var totals = await CalculateShiftTotalsAsync(shift.Id, ct);

        var declaredCash = request.DeclaredCash!.Value;
        var expectedCash = ExpectedCash(shift, totals);
        var difference = declaredCash - expectedCash;

        shift.DeclaredCash = declaredCash;
        shift.ExpectedCash = expectedCash;
        shift.CardTotal = totals.CardTotal;
        shift.TipsTotal = totals.TipsTotal;
        shift.Difference = difference;
        shift.CloseReason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
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

    /// <summary>
    /// PMC-9 AC2: expected_cash = opening_float + cash received - change_given. Cash breakdowns are already capped to
    /// what the bill needed (received - change), so their sum is exactly the cash that stayed in the drawer, tips included.
    /// </summary>
    private static decimal ExpectedCash(CashShift shift, ShiftTotals totals) => shift.InitialFund + totals.CashTotal;

    /// <summary>One server-side aggregation over the shift's completed payments: no transaction is loaded into memory.</summary>
    private async Task<ShiftTotals> CalculateShiftTotalsAsync(Guid shiftId, CancellationToken ct)
    {
        var totals = await db.Transactions.AsNoTracking()
            .Where(t => t.CashShiftId == shiftId && t.Status == TransactionStatus.Completed)
            .SelectMany(t => t.Payments.Select(p => new { p.Method, p.Amount, t.Tip, t.Total }))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Cash = g.Sum(x => x.Method == PaymentMethod.Cash ? x.Amount : 0m),
                Card = g.Sum(x => x.Method != PaymentMethod.Cash ? x.Amount : 0m),
                CardCount = g.Sum(x => x.Method != PaymentMethod.Cash ? 1 : 0),
                // Each payment carries the tip in proportion to its amount; Total > 0 always, since it is the sum of payments.
                CardTips = g.Sum(x => x.Method != PaymentMethod.Cash ? x.Tip * x.Amount / x.Total : 0m),
                Tips = g.Sum(x => x.Tip * x.Amount / x.Total)
            })
            .SingleOrDefaultAsync(ct);

        return totals is null
            ? new ShiftTotals(0m, 0m, 0, 0m, 0m)
            : new ShiftTotals(totals.Cash, totals.Card, totals.CardCount, Money(totals.CardTips), Money(totals.Tips));
    }

    private static decimal Money(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static OpenShiftResult AlreadyOpen() =>
        new(OpenShiftOutcome.AlreadyOpen, Error: "Ya existe un turno de caja abierto");

    private static CloseShiftResult NoOpenShift() =>
        new(CloseShiftOutcome.NoOpenShift, Error: "No hay un turno de caja abierto para este usuario");
}
