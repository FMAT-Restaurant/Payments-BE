using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class PendingBillService(IDistributedCache cache, IPendingSessionIndex pendingSessions)
{
    private static readonly DistributedCacheEntryOptions Ttl = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
    };

    /// <summary>Saves (or overwrites) the session snapshot. Returns false if the event was already processed or is older than the saved snapshot.</summary>
    public async Task<bool> SaveSnapshotAsync(DinningSessionReadyEvent message, CancellationToken ct = default)
    {
        // ponytail: check-then-set, two concurrent deliveries of the same eventId can both write (same data, harmless).
        var eventKey = $"payments:event:{message.EventId}";
        if (await cache.GetAsync(eventKey, ct) is not null)
        {
            return false;
        }

        // Out-of-order delivery: an older event must not overwrite a newer snapshot.
        var current = await GetAsync(message.DinningSessionId, ct);
        if (current is not null && message.Timestamp < current.Timestamp)
        {
            await cache.SetStringAsync(eventKey, "1", Ttl, ct);
            return false;
        }

        await cache.SetStringAsync(BillKey(message.DinningSessionId), JsonSerializer.Serialize(message), Ttl, ct);
        await cache.SetStringAsync(eventKey, "1", Ttl, ct);
        // Scored by when Pagos saw the change, not by the event timestamp: a late delivery must still show up in PMC-27's ?since.
        await pendingSessions.AddAsync(message.DinningSessionId, DateTimeOffset.UtcNow);
        return true;
    }

    public async Task<DinningSessionReadyEvent?> GetAsync(Guid dinningSessionId, CancellationToken ct = default)
    {
        var json = await cache.GetStringAsync(BillKey(dinningSessionId), ct);
        return json is null ? null : JsonSerializer.Deserialize<DinningSessionReadyEvent>(json);
    }

    private static string BillKey(Guid dinningSessionId) => $"payments:bill:{dinningSessionId}";
}
