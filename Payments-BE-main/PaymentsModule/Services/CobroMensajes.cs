namespace PaymentsModule.Services;

public static class CobroMensajes
{
    public const string FondosInsuficientes = "Fondos insuficientes para cubrir el total";
    public const string ExcedenteNoAbsorbible = "El excedente solo puede devolverse como cambio de pagos en efectivo";
    public const string YaPagado = "La transacción ya ha sido pagada previamente";
    public const string TurnoCajaInactivo = "El cajero no tiene un turno de caja activo";
    public const string TarjetaRechazada = "La pasarela bancaria rechazó la tarjeta";
    public const string PasarelaNoDisponible = "La pasarela bancaria no está disponible, intente de nuevo";
    public const string ErrorInterno = "No fue posible procesar el cobro";
    public const string CobroMixtoOk = "Cobro mixto consolidado correctamente";
    public const string CobroOk = "Cobro consolidado correctamente";
}
