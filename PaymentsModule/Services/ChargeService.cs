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
    ElectronicOverpayment,
    CardDeclined,
    GatewayUnavailable
}

public record ChargeResult(ChargeOutcome Outcome, ChargeResponse? Response = null, string? Error = null, decimal? Deficit = null);

public class ChargeService(
    PaymentsDbContext db,
    PendingBillService bills,
    IPublishEndpoint publishEndpoint,
    IPaymentGateway gateway,
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

        // The pending bill from Órdenes (PMC-5) is the source of truth for prices; the client snapshot is not trusted.
        var bill = await bills.GetAsync(request.DinningSessionId, ct);
        if (bill is null)
        {
            return new(ChargeOutcome.BillNotFound, Error: "No pending bill was found for this dinning session.");
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

        var payments = ApplyPayments(request.Payments, total);

        // Card payments are charged in the bank gateway before the DB transaction opens, so the shift row lock is never held while waiting on the network.
        var charges = new List<GatewayCharge>();
        if (payments.Any(p => p.Method != PaymentMethod.Cash))
        {
            // Cheap unlocked check so cards are not charged and refunded only because the cashier has no open shift; PersistAsync re-checks under the lock.
            var hasOpenShift = await db.CashShifts.AsNoTracking()
                .AnyAsync(s => s.CashierId == cashierId && s.Status == CashShiftStatus.Open, ct);
            if (!hasOpenShift)
            {
                return new(ChargeOutcome.NoOpenShift, Error: "The cashier has no open cash shift.");
            }

            var failure = await ChargeCardsAsync(request.DinningSessionId, payments, charges, ct);
            if (failure is not null)
            {
                return failure;
            }
        }

        ChargeResult result;
        try
        {
            result = await PersistAsync(request, cashierId, bill, total, payments, ct);
        }
        catch
        {
            // PersistAsync rolled the transaction back, so the cards are returned too; the exception still reaches the global handler (500).
            if (charges.Count > 0 && !await WasPersistedAsync(request.DinningSessionId))
            {
                await ReverseAsync(charges);
            }

            throw;
        }

        // The transaction (and its shift lock) is already released here, so refunds never run under the lock.
        if (result.Outcome == ChargeOutcome.AlreadyPaid)
        {
            await ReverseUnclaimedAsync(request.DinningSessionId, charges);
        }
        else if (result.Outcome != ChargeOutcome.Completed)
        {
            await ReverseAsync(charges);
        }

        return result;
    }

    /// <summary>Opens the ReadCommitted transaction, locks the shift, and saves the sale plus its outbox event atomically.</summary>
    private async Task<ChargeResult> PersistAsync(
        ChargeRequest request,
        string cashierId,
        DinningSessionReadyEvent bill,
        decimal total,
        List<PaymentBreakdown> payments,
        CancellationToken ct)
    {
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
            Payments = payments
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

        var change = request.Payments.Sum(p => p.Amount) - total;
        return new(ChargeOutcome.Completed, new ChargeResponse(
            transaction.Folio, transaction.Total, transaction.Status, change, "Cobro procesado exitosamente"));
    }

    /// <summary>Charges every card payment. On a decline or an outage it reverses what was charged and returns the failure result.</summary>
    private async Task<ChargeResult?> ChargeCardsAsync(Guid sessionId, List<PaymentBreakdown> payments, List<GatewayCharge> charges, CancellationToken ct)
    {
        foreach (var payment in payments.Where(p => p.Method != PaymentMethod.Cash))
        {
            var charge = new GatewayCharge(sessionId, payment.AuthorizationReference ?? string.Empty, payment.Amount);
            try
            {
                var approval = await gateway.ChargeAsync(charge, ct);
                if (!approval.Approved)
                {
                    logger.LogWarning(
                        "Gateway declined card payment {Reference} of session {SessionId}: {Reason}",
                        charge.AuthorizationReference, sessionId, approval.DeclineReason);
                    await ReverseAsync(charges);
                    return new(ChargeOutcome.CardDeclined, Error: "The payment gateway declined the card.");
                }

                charges.Add(charge);
            }
            catch (PaymentGatewayException ex)
            {
                logger.LogError(ex, "Payment gateway unavailable for session {SessionId}", sessionId);

                // Unknown outcome: the charge may have been applied even though no answer arrived, so it is reversed as well.
                await ReverseAsync([.. charges, charge]);
                return new(ChargeOutcome.GatewayUnavailable, Error: "The payment gateway is unavailable, try again.");
            }
            catch (OperationCanceledException)
            {
                await ReverseAsync([.. charges, charge]);
                throw;
            }
        }

        return null;
    }

    /// <summary>Best effort: refunds each charge and keeps going if one refund fails, logging it for manual reconciliation.</summary>
    private async Task ReverseAsync(IReadOnlyCollection<GatewayCharge> charges)
    {
        foreach (var charge in charges)
        {
            try
            {
                await gateway.ReverseAsync(charge, CancellationToken.None);
                logger.LogWarning("Card payment {Reference} of session {SessionId} reversed", charge.AuthorizationReference, charge.DinningSessionId);
            }
            catch (Exception ex)
            {
                logger.LogCritical(
                    ex,
                    "Could NOT reverse card payment {Reference} of session {SessionId}; manual reconciliation required",
                    charge.AuthorizationReference,
                    charge.DinningSessionId);
            }
        }
    }

    /// <summary>The gateway deduplicates by idempotency key, so a duplicate carrying the same reference shares the winner's charge and must not be reversed.</summary>
    private async Task ReverseUnclaimedAsync(Guid sessionId, List<GatewayCharge> charges)
    {
        if (charges.Count == 0)
        {
            return;
        }

        db.ChangeTracker.Clear();
        var claimed = await db.Transactions.AsNoTracking()
            .Where(t => t.DinningSessionId == sessionId)
            .SelectMany(t => t.Payments)
            .Select(p => p.AuthorizationReference)
            .ToListAsync(CancellationToken.None);

        await ReverseAsync(charges.Where(c => !claimed.Contains(c.AuthorizationReference)).ToList());
    }

    /// <summary>Guards against refunding a payment whose commit actually succeeded. If the check itself fails, the cards are refunded and the doubt is logged.</summary>
    private async Task<bool> WasPersistedAsync(Guid sessionId)
    {
        try
        {
            db.ChangeTracker.Clear();
            return await db.Transactions.AsNoTracking().AnyAsync(t => t.DinningSessionId == sessionId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not verify whether session {SessionId} was persisted; its card payments will be reversed", sessionId);
            return false;
        }
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
