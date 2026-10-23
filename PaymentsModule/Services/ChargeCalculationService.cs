using PaymentsModule.Models;

namespace PaymentsModule.Services;

public enum ChargeCalculationStatus
{
    Covered,
    InsufficientFunds,
    ElectronicOverpayment
}

/// <summary>
/// Result of splitting the received payments against the amount due. Every amount is <see cref="decimal"/> (RNF-05).
/// </summary>
/// <param name="Breakdown">Payments capped to what was still owed; their sum equals the amount due when <see cref="Status"/> is <see cref="ChargeCalculationStatus.Covered"/>.</param>
/// <param name="ChangeGiven">Cash to hand back to the customer; never stored as income.</param>
/// <param name="Deficit">Amount still missing when <see cref="Status"/> is <see cref="ChargeCalculationStatus.InsufficientFunds"/>.</param>
public record ChargeCalculation(
    ChargeCalculationStatus Status,
    IReadOnlyList<PaymentBreakdown> Breakdown,
    decimal ChangeGiven,
    decimal Deficit);

/// <summary>Pure charge math (PMC-13): change calculation and the capped payment split. No I/O.</summary>
public interface IChargeCalculationService
{
    ChargeCalculation Calculate(IEnumerable<PaymentRequest> payments, decimal amountDue);
}

public class ChargeCalculationService : IChargeCalculationService
{
    public ChargeCalculation Calculate(IEnumerable<PaymentRequest> payments, decimal amountDue)
    {
        var received = payments.ToList();
        var total = received.Sum(p => p.Amount);
        if (total < amountDue)
        {
            return new(ChargeCalculationStatus.InsufficientFunds, [], 0m, amountDue - total);
        }

        // Only electronic payments on their own exceeding the amount due are rejected: cards cannot produce change, cash can absorb it.
        var electronic = received.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.Amount);
        if (electronic > amountDue)
        {
            return new(ChargeCalculationStatus.ElectronicOverpayment, [], 0m, 0m);
        }

        var breakdown = Split(received, amountDue);
        // Electronic payments never exceed the amount due here, so the whole excess is cash and goes back as change.
        return new(ChargeCalculationStatus.Covered, breakdown, total - amountDue, 0m);
    }

    /// <summary>Caps each payment with <c>Math.Min(amount, remaining)</c> so change is never stored as income.</summary>
    private static List<PaymentBreakdown> Split(IEnumerable<PaymentRequest> payments, decimal amountDue)
    {
        var remaining = amountDue;
        var breakdown = new List<PaymentBreakdown>();
        // Electronic payments first: they can never be capped, so cash absorbs any excess.
        foreach (var payment in payments.OrderBy(p => p.Method == PaymentMethod.Cash))
        {
            var applied = Math.Min(payment.Amount, remaining);
            if (applied == 0m)
            {
                // Cash that contributed nothing (e.g. a card already covered the bill) is returned whole as change.
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
}
