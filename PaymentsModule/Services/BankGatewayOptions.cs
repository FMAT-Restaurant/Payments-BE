namespace PaymentsModule.Services;

/// <summary>
/// Settings of the external bank gateway used to refund card payments (PMC-12).
/// The mock is off by default so an environment without a configured gateway fails with 502 instead of pretending to refund; only appsettings.Development.json turns it on.
/// </summary>
public class BankGatewayOptions
{
    public const string SectionName = "BankGateway";

    public string BaseUrl { get; set; } = "http://bank-gateway.invalid/";

    /// <summary>When true, a local stub answers instead of a real HTTP call. Must stay false in any environment with a real gateway.</summary>
    public bool UseMock { get; set; }

    /// <summary>HTTP status the mock answers with: 200 simulates a refund, 503 simulates an outage.</summary>
    public int MockStatusCode { get; set; } = StatusCodes.Status200OK;

    /// <summary>Retries after the first attempt (exponential backoff). Polly requires at least 1.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Base delay of the exponential backoff.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Total time budget of one refund call, retries included.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
