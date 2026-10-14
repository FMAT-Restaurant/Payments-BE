using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class CuentaPendienteService(IDistributedCache cache)
{
    private static readonly DistributedCacheEntryOptions Ttl = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
    };

    /// <summary>Saves (or overwrites) the session snapshot. Returns false if the event was already processed or is older than the saved snapshot.</summary>
    public async Task<bool> GuardarSnapshotAsync(DinningSessionReadyEvent evento, CancellationToken ct = default)
    {
        // ponytail: check-then-set, two concurrent deliveries of the same eventId can both write (same data, harmless).
        var eventoKey = $"pagos:evento:{evento.EventId}";
        if (await cache.GetAsync(eventoKey, ct) is not null)
        {
            return false;
        }

        // Out-of-order delivery: an older event must not overwrite a newer snapshot.
        var actual = await ObtenerAsync(evento.DinningSessionId, ct);
        if (actual is not null && evento.Timestamp < actual.Timestamp)
        {
            await cache.SetStringAsync(eventoKey, "1", Ttl, ct);
            return false;
        }

        await cache.SetStringAsync(CuentaKey(evento.DinningSessionId), JsonSerializer.Serialize(evento), Ttl, ct);
        await cache.SetStringAsync(eventoKey, "1", Ttl, ct);
        return true;
    }

    public async Task<DinningSessionReadyEvent?> ObtenerAsync(Guid dinningSessionId, CancellationToken ct = default)
    {
        var json = await cache.GetStringAsync(CuentaKey(dinningSessionId), ct);
        return json is null ? null : JsonSerializer.Deserialize<DinningSessionReadyEvent>(json);
    }

    private static string CuentaKey(Guid dinningSessionId) => $"pagos:cuenta:{dinningSessionId}";
}
