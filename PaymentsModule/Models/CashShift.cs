using System.Text.Json.Serialization;

namespace PaymentsModule.Models;

public class CashShift
{
    public Guid Id { get; set; }
    public required string CashierId { get; set; }

    /// <summary>Display name from the JWT at opening (PMC-8 AC3); null while Auth does not send the claim.</summary>
    public string? CashierName { get; set; }

    public decimal InitialFund { get; set; }
    public decimal? DeclaredCash { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CardTotal { get; set; }
    public decimal? TipsTotal { get; set; }
    public decimal? Difference { get; set; }

    /// <summary>Optional reason sent by the cashier at close, usually for an unbalanced count (PMC-8 AC6).</summary>
    public string? CloseReason { get; set; }
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

// Wire contract of PMC-8 and PMC-9 (05/11/2026): English snake_case.
public record OpenCashShiftRequest(
    [property: JsonPropertyName("opening_float")] decimal? InitialFund);

public record OpenCashShiftResponse(
    [property: JsonPropertyName("cash_shift_id")] Guid CashShiftId,
    [property: JsonPropertyName("status")] CashShiftStatus Status,
    [property: JsonPropertyName("message")] string Message);

public record CloseCashShiftRequest(
    [property: JsonPropertyName("declared_cash")] decimal? DeclaredCash,
    [property: JsonPropertyName("reason")] string? Reason = null);

public record CloseCashShiftResponse(
    [property: JsonPropertyName("cash_shift_id")] Guid CashShiftId,
    [property: JsonPropertyName("status")] CashShiftStatus Status,
    [property: JsonPropertyName("difference")] decimal Difference,
    [property: JsonPropertyName("message")] string Message);

public record CashShiftSummaryResponse(
    [property: JsonPropertyName("cash_shift_id")] Guid CashShiftId,
    [property: JsonPropertyName("cashier_name")] string? CashierName,
    [property: JsonPropertyName("status")] CashShiftStatus Status,
    [property: JsonPropertyName("opened_at")] DateTimeOffset OpenedAt,
    [property: JsonPropertyName("opening_float")] decimal OpeningFloat,
    [property: JsonPropertyName("expected_cash")] decimal ExpectedCash,
    [property: JsonPropertyName("card_total")] decimal CardTotal,
    [property: JsonPropertyName("card_transaction_count")] int CardTransactionCount,
    [property: JsonPropertyName("retained_tips")] decimal RetainedTips,
    [property: JsonPropertyName("calculated_at")] DateTimeOffset CalculatedAt);
