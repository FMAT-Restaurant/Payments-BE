using System.Security.Claims;

namespace PaymentsModule.Services;

/// <summary>Identidad del cajero autenticado, siempre tomada de los claims del JWT.</summary>
public interface ICajeroContext
{
    string? CajeroId { get; }
}

public sealed class HttpCajeroContext(IHttpContextAccessor httpContextAccessor) : ICajeroContext
{
    // "sub" es el claim estándar del JWT (MapInboundClaims está desactivado); NameIdentifier cubre tokens que ya lo traen mapeado.
    public string? CajeroId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            var id = user?.FindFirstValue("sub") ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
    }
}
