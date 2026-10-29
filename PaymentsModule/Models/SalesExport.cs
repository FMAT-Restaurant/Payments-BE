namespace PaymentsModule.Models;

/// <summary>Query string of GET /api/v1/pagos/exportar (<c>inicio</c> and <c>fin</c>). Nullable so a missing value reaches the validator.</summary>
public record SalesExportQuery(DateTimeOffset? Start, DateTimeOffset? End);

/// <summary>One CSV line: a sale with its payment breakdown aggregated into cash and cards.</summary>
public record SalesExportRow(
    string Folio,
    DateTimeOffset CreatedAt,
    TransactionStatus Status,
    decimal Subtotal,
    decimal Taxes,
    decimal Tip,
    decimal Total,
    decimal Cash,
    decimal Cards);
