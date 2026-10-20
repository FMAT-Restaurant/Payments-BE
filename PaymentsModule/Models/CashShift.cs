namespace PaymentsModule.Models;

// Minimal shape agreed with PMC-8 (open/close endpoints) so both stories share one table.
public class CashShift
{
    public Guid Id { get; set; }
    public required string CashierId { get; set; }
    public decimal InitialFund { get; set; }
    public CashShiftStatus Status { get; set; } = CashShiftStatus.Open;
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }

    // Optimistic concurrency token (SQL Server rowversion), required by PMC-8.
    public byte[] RowVersion { get; set; } = [];
}

public enum CashShiftStatus
{
    Open,
    Closed,
    Unbalanced
}
