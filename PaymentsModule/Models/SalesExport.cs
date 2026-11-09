using Microsoft.AspNetCore.Mvc;

namespace PaymentsModule.Models;

/// <summary>
/// Query string parameters for sales export (PMC-15).
/// Contract agreed with PO (05/11/2026): English parameters (start_date, end_date), semi-open window [start_date, end_date).
/// </summary>
public class SalesExportQuery
{
    [FromQuery(Name = "start_date")]
    public DateTimeOffset? StartDate { get; set; }

    [FromQuery(Name = "fecha_inicio")]
    public DateTimeOffset? FechaInicio { set => StartDate ??= value; }

    [FromQuery(Name = "inicio")]
    public DateTimeOffset? Inicio { set => StartDate ??= value; }

    [FromQuery(Name = "end_date")]
    public DateTimeOffset? EndDate { get; set; }

    [FromQuery(Name = "fecha_fin")]
    public DateTimeOffset? FechaFin { set => EndDate ??= value; }

    [FromQuery(Name = "fin")]
    public DateTimeOffset? Fin { set => EndDate ??= value; }

    public SalesExportQuery() { }

    public SalesExportQuery(DateTimeOffset? startDate, DateTimeOffset? endDate)
    {
        StartDate = startDate;
        EndDate = endDate;
    }
}

/// <summary>One CSV line: a sale with its payment breakdown aggregated into cash and cards (no status column per PO agreement 05/11/2026).</summary>
public record SalesExportRow(
    string Folio,
    DateTimeOffset CreatedAt,
    decimal Subtotal,
    decimal Taxes,
    decimal Tip,
    decimal Total,
    decimal Cash,
    decimal Cards);
