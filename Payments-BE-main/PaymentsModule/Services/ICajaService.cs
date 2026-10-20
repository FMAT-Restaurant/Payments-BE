using PaymentsModule.Models.Dtos;

namespace PaymentsModule.Services;

public enum CajaError
{
    None,
    TurnoYaAbierto,
    SinTurnoAbierto,
    ConflictoConcurrencia,
}

public sealed record CajaResult<T>(T? Valor, CajaError Error, string? Mensaje)
    where T : class
{
    public bool EsExitoso => Error == CajaError.None;

    public static CajaResult<T> Exito(T valor) => new(valor, CajaError.None, null);

    public static CajaResult<T> Falla(CajaError error, string mensaje) => new(null, error, mensaje);
}

public interface ICajaService
{
    Task<CajaResult<AbrirCajaResponseDTO>> AbrirAsync(decimal fondoInicial, string cajeroId, CancellationToken ct);

    Task<CajaResult<CerrarCajaResponseDTO>> CerrarAsync(decimal efectivoDeclarado, string cajeroId, CancellationToken ct);
}
