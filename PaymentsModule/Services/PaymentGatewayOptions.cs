namespace PaymentsModule.Services;

public class PaymentGatewayOptions
{
    public const string SectionName = "PaymentGateway";

    /// <summary>Base address of the bank gateway. When empty, no gateway is called (see <see cref="UnconfiguredPaymentGateway"/>).</summary>
    public string? BaseUrl { get; set; }

    public int MaxRetryAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(300);
}
