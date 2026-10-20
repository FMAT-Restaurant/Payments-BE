using PaymentsModule.Models.Dtos;

namespace PaymentsModule.Services;

public enum CobroError
{
    None,
    FondosInsuficientes,
    ExcedenteNoAbsorbible,
    TurnoCajaInactivo,
    YaPagado,
    TarjetaRechazada,
    PasarelaNoDisponible,
    ErrorInterno,
}

public sealed record CobroResult(CobroResponseDTO? Respuesta, CobroError Error, string? Mensaje)
{
    public bool EsExitoso => Error == CobroError.None;

    public static CobroResult Exito(CobroResponseDTO respuesta) => new(respuesta, CobroError.None, null);

    public static CobroResult Falla(CobroError error, string mensaje) => new(null, error, mensaje);
}

public interface ICobroService
{
    Task<CobroResult> ProcesarCobroAsync(CobroRequestDTO request, string cajeroId, CancellationToken ct);
}
