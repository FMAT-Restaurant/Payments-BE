using PaymentsModule.Services;

namespace PaymentsModule.Tests;

// In-memory gateway for ChargeServiceTests: records every charge and reversal and can decline or fail on demand.
public class FakePaymentGateway : IPaymentGateway
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _declined = [];
    private readonly List<GatewayCharge> _charged = [];
    private readonly List<GatewayCharge> _reversed = [];

    public bool Unavailable { get; set; }

    public IReadOnlyList<GatewayCharge> Charged
    {
        get
        {
            lock (_gate)
            {
                return [.. _charged];
            }
        }
    }

    public IReadOnlyList<GatewayCharge> Reversed
    {
        get
        {
            lock (_gate)
            {
                return [.. _reversed];
            }
        }
    }

    public void Decline(string authorizationReference)
    {
        lock (_gate)
        {
            _declined.Add(authorizationReference);
        }
    }

    public Task<GatewayChargeResult> ChargeAsync(GatewayCharge charge, CancellationToken ct = default)
    {
        if (Unavailable)
        {
            throw new PaymentGatewayException("Simulated gateway outage.");
        }

        lock (_gate)
        {
            if (_declined.Contains(charge.AuthorizationReference))
            {
                return Task.FromResult(new GatewayChargeResult(false, "Simulated decline."));
            }

            _charged.Add(charge);
        }

        return Task.FromResult(new GatewayChargeResult(true));
    }

    public Task ReverseAsync(GatewayCharge charge, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _reversed.Add(charge);
        }

        return Task.CompletedTask;
    }
}
