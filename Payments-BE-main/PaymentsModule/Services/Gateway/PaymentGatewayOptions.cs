using System.ComponentModel.DataAnnotations;

namespace PaymentsModule.Services.Gateway;

public sealed class PaymentGatewayOptions
{
    public const string SectionName = "PaymentGateway";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;
}
