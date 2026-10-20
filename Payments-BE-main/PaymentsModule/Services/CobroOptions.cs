namespace PaymentsModule.Services;

public sealed class CobroOptions
{
    public const string SectionName = "Cobros";

    /// <summary>Zona horaria (IANA/Windows) usada para la fecha del folio TX-yyyyMMdd-NNN.</summary>
    public string TimeZoneId { get; set; } = "UTC";
}
