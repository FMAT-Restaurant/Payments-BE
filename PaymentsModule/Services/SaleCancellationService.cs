using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public enum CancelSaleOutcome
{
    Cancelled,
    NotFound,
    AlreadyCancelled,
    MissingAuthorizationReference,
    GatewayFailure
}

public record CancelSaleResult(CancelSaleOutcome Outcome, CancelSaleResponse? Response = null, string? Error = null);

public class SaleCancellationService(PaymentsDbContext db, IBankGatewayClient gateway, ILogger<SaleCancellationService> logger)
{
    public const string SuccessMessage = "Extorno procesado exitosamente. Se generó el ticket de cancelación.";
    public const string AlreadyCancelledMessage = "The transaction has already been cancelled";

    /// <summary>Cancels a completed sale. <paramref name="authorizerId"/> must come from the caller's JWT, never from the payload.</summary>
    public async Task<CancelSaleResult> CancelAsync(string folio, string reason, string authorizerId, CancellationToken ct = default)
    {
        await using var dbTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // UPDLOCK on the sale row: a double click waits here and then finds it cancelled, so the card is never refunded twice.
        var sale = await db.Transactions
            .FromSqlInterpolated($"SELECT * FROM Transactions WITH (UPDLOCK, ROWLOCK) WHERE Folio = {folio}")
            .SingleOrDefaultAsync(ct);

        // The folio is an exact, case-sensitive string (PMC-12-P7), but SQL Server's default collation ignores case and trailing spaces.
        if (sale is null || !string.Equals(sale.Folio, folio, StringComparison.Ordinal))
        {
            return new(CancelSaleOutcome.NotFound, Error: $"The ticket '{folio}' does not exist.");
        }

        if (sale.Status == TransactionStatus.Cancelled)
        {
            return AlreadyCancelled();
        }

        await db.Entry(sale).Collection(t => t.Payments).LoadAsync(ct);

        var cardPayments = sale.Payments.Where(p => p.Method != PaymentMethod.Cash).ToList();

        // Checked before the first refund so a missing reference never leaves some cards refunded and others not.
        if (cardPayments.Any(p => string.IsNullOrWhiteSpace(p.AuthorizationReference)))
        {
            return new(CancelSaleOutcome.MissingAuthorizationReference,
                Error: "A card payment has no authorization reference, so the refund cannot be processed.");
        }

        var references = new List<string>();
        var refunded = 0m;

        // Point of no return: from the first refund to the COMMIT the caller's token is ignored. If the client disconnects after
        // card 1 was refunded, aborting would roll the sale back (still COMPLETED) with money already returned and no audit row.
        // Every gateway call is bounded by the Polly timeout, so this cannot hang.
        var commitToken = CancellationToken.None;

        // One refund per card payment (cash never goes through the gateway). Any failure aborts: leaving the transaction
        // without COMMIT rolls everything back and the sale stays COMPLETED.
        foreach (var payment in cardPayments)
        {
            try
            {
                // Stable key per payment: retrying a cancellation after a failed COMMIT does not refund the card again.
                var refund = await gateway.RefundAsync(
                    new BankRefundRequest(sale.Folio, payment.AuthorizationReference!, payment.Amount, $"{sale.Id}:{payment.Id}"), commitToken);
                references.Add(refund.RefundReference);
                refunded += payment.Amount;
            }
            catch (BankGatewayException ex)
            {
                logger.LogError(ex,
                    "Refund failed while cancelling {Folio}; {ConfirmedRefunds} refund(s) were already confirmed by the gateway and need reconciliation.",
                    sale.Folio, references.Count);
                return new(CancelSaleOutcome.GatewayFailure,
                    Error: "The bank gateway could not process the refund, so the sale was not cancelled.");
            }
        }

        var cancelledAt = DateTimeOffset.UtcNow;
        sale.Status = TransactionStatus.Cancelled;
        db.SaleCancellations.Add(new SaleCancellation
        {
            TransactionId = sale.Id,
            AuthorizerId = authorizerId,
            Reason = reason,
            CancelledAt = cancelledAt,
            GatewayReference = references.Count == 0 ? null : string.Join(",", references),
            RefundedAmount = refunded
        });

        try
        {
            await db.SaveChangesAsync(commitToken);
        }
        // 2601/2627: duplicate key on the unique index of SaleCancellations.TransactionId (last line of defence).
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return AlreadyCancelled();
        }

        await dbTransaction.CommitAsync(commitToken);

        return new(CancelSaleOutcome.Cancelled, new CancelSaleResponse(sale.Folio, sale.Status, cancelledAt, SuccessMessage));
    }

    private static CancelSaleResult AlreadyCancelled() =>
        new(CancelSaleOutcome.AlreadyCancelled, Error: AlreadyCancelledMessage);
}
