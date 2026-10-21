using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

public class CashShift
{
    public Guid Id { get; set; }
    public required string CashierId { get; set; }
    public decimal InitialFund { get; set; }
    public decimal? DeclaredCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CardTotal { get; set; }
    public decimal? TipsTotal { get; set; }
    public decimal? Difference { get; set; }
    public CashShiftStatus Status { get; set; } = CashShiftStatus.Open;
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    // Optimistic concurrency token (SQL Server rowversion), required by PMC-8.
    public byte[] RowVersion { get; set; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter<CashShiftStatus>))]
public enum CashShiftStatus
{
    [JsonStringEnumMemberName("ABIERTO")] Open,
    [JsonStringEnumMemberName("CERRADO")] Closed,
    [JsonStringEnumMemberName("DESCUADRADO")] Unbalanced
}

public record OpenCashShiftRequest(
    [property: JsonPropertyName("fondo_inicial")] decimal? InitialFund);

public record OpenCashShiftResponse(
    [property: JsonPropertyName("turno_caja_id")] Guid CashShiftId,
    [property: JsonPropertyName("estado")] CashShiftStatus Status,
    [property: JsonPropertyName("mensaje")] string Message);

public record CloseCashShiftRequest(
    [property: JsonPropertyName("efectivo_declarado")] decimal? DeclaredCash);

public record CloseCashShiftResponse(
    [property: JsonPropertyName("turno_caja_id")] Guid CashShiftId,
    [property: JsonPropertyName("estado")] CashShiftStatus Status,
    [property: JsonPropertyName("diferencia")] decimal Difference,
    [property: JsonPropertyName("mensaje")] string Message);
