namespace PaymentsModule.Models.Entities;

public enum MetodoPago
{
    EFECTIVO,
    TARJETA_CREDITO,
}

public enum EstadoTransaccion
{
    COMPLETADO,
    CANCELADO,
}

public enum EstadoTurno
{
    ABIERTO,
    CERRADO,
    DESCUADRADO,
}

public class TurnoCaja
{
    public Guid Id { get; set; }

    public string CajeroId { get; set; } = string.Empty;

    public EstadoTurno Estado { get; set; }

    public decimal FondoInicial { get; set; }

    public DateTime FechaAperturaUtc { get; set; }

    public DateTime? FechaCierreUtc { get; set; }

    /// <summary>Efectivo que el sistema esperaba en caja al cierre (fondo inicial + cobros en efectivo).</summary>
    public decimal? EfectivoEsperado { get; set; }

    public decimal? EfectivoDeclarado { get; set; }

    /// <summary>Declarado - esperado: positivo = sobrante, negativo = faltante.</summary>
    public decimal? Diferencia { get; set; }

    /// <summary>Total cobrado con tarjeta durante el turno (informativo, no entra al cuadre de efectivo).</summary>
    public decimal? VentasTarjeta { get; set; }

    /// <summary>Token de concurrencia optimista.</summary>
    public byte[] RowVersion { get; set; } = [];
}

public class TransaccionPago
{
    public Guid Id { get; set; }

    public string Folio { get; set; } = string.Empty;

    public Guid DinningSessionId { get; set; }

    public Guid TurnoCajaId { get; set; }

    public decimal Total { get; set; }

    public EstadoTransaccion Estado { get; set; }

    public DateTime FechaCreacionUtc { get; set; }

    public List<DetalleTransaccion> Detalles { get; set; } = [];

    public List<DesglosePago> Desgloses { get; set; } = [];
}

public class DetalleTransaccion
{
    public Guid Id { get; set; }

    public Guid TransaccionPagoId { get; set; }

    public string NombrePlatillo { get; set; } = string.Empty;

    public int Cantidad { get; set; }

    public decimal PrecioUnitario { get; set; }

    public decimal Importe { get; set; }
}

public class DesglosePago
{
    public Guid Id { get; set; }

    public Guid TransaccionPagoId { get; set; }

    public MetodoPago MetodoPago { get; set; }

    /// <summary>Monto efectivamente aplicado a la cuenta (en efectivo ya descontado el cambio).</summary>
    public decimal Monto { get; set; }

    public string? ReferenciaAutorizacion { get; set; }
}

/// <summary>Contador diario usado para generar el consecutivo del folio (TX-yyyyMMdd-NNN).</summary>
public class FolioContador
{
    public DateOnly Fecha { get; set; }

    public int Ultimo { get; set; }
}
