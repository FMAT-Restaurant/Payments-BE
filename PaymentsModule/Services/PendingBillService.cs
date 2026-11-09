using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using PaymentsModule.Models;

namespace PaymentsModule.Services;

public class PendingBillService(IDistributedCache cache)
{
    private static readonly DistributedCacheEntryOptions Ttl = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
    };

    public const string IndexKey = "payments:pending-bills:index";

    /// <summary>Saves (or overwrites) the session snapshot. Returns false if the event was already processed or is older than the saved snapshot.</summary>
    public async Task<bool> SaveSnapshotAsync(DinningSessionReadyEvent message, CancellationToken ct = default)
    {
        // Check-then-set, two concurrent deliveries of the same eventId can both write (same data, harmless).
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

        await AddToActiveIndexAsync(message.DinningSessionId, ct);
        return true;
    }

    public async Task<DinningSessionReadyEvent?> GetAsync(Guid dinningSessionId, CancellationToken ct = default)
    {
        var json = await cache.GetStringAsync(BillKey(dinningSessionId), ct);
        return json is null ? null : JsonSerializer.Deserialize<DinningSessionReadyEvent>(json);
    }

    /// <summary>Returns the pending bill mapped to the DTO contract, or null if not found (HU-FE-02 / AC2).</summary>
    public async Task<PendingBillResponse?> GetPendingBillAsync(Guid dinningSessionId, CancellationToken ct = default)
    {
        var bill = await GetAsync(dinningSessionId, ct);
        return bill is null ? null : PendingBillResponse.FromEvent(bill);
    }

    /// <summary>
    /// Returns all active pending bills by reading the secondary Set index without scanning keys with KEYS * (AC2).
    /// </summary>
    public async Task<IReadOnlyList<PendingBillResponse>> GetAllPendingAsync(CancellationToken ct = default)
    {
        var indexJson = await cache.GetStringAsync(IndexKey, ct);
        if (string.IsNullOrWhiteSpace(indexJson))
        {
            return [];
        }

        var set = JsonSerializer.Deserialize<HashSet<Guid>>(indexJson);
        if (set is null || set.Count == 0)
        {
            return [];
        }

        var list = new List<PendingBillResponse>();
        var expiredOrRemoved = new List<Guid>();

        foreach (var id in set)
        {
            var bill = await GetAsync(id, ct);
            if (bill is not null)
            {
                list.Add(PendingBillResponse.FromEvent(bill));
            }
            else
            {
                expiredOrRemoved.Add(id);
            }
        }

        // Clean up expired or paid entries from the index if any were found
        if (expiredOrRemoved.Count > 0)
        {
            foreach (var id in expiredOrRemoved)
            {
                set.Remove(id);
            }
            await cache.SetStringAsync(IndexKey, JsonSerializer.Serialize(set), Ttl, ct);
        }

        return list.OrderByDescending(b => b.Timestamp).ToList();
    }

    /// <summary>Removes the session from the cache and from the active index when paid or cancelled (AC2).</summary>
    public async Task RemoveAsync(Guid dinningSessionId, CancellationToken ct = default)
    {
        await cache.RemoveAsync(BillKey(dinningSessionId), ct);

        var indexJson = await cache.GetStringAsync(IndexKey, ct);
        if (!string.IsNullOrWhiteSpace(indexJson))
        {
            var set = JsonSerializer.Deserialize<HashSet<Guid>>(indexJson);
            if (set is not null && set.Remove(dinningSessionId))
            {
                await cache.SetStringAsync(IndexKey, JsonSerializer.Serialize(set), Ttl, ct);
            }
        }
    }

    private async Task AddToActiveIndexAsync(Guid sessionId, CancellationToken ct)
    {
        var indexJson = await cache.GetStringAsync(IndexKey, ct);
        var set = !string.IsNullOrWhiteSpace(indexJson)
            ? JsonSerializer.Deserialize<HashSet<Guid>>(indexJson) ?? []
            : new HashSet<Guid>();

        if (set.Add(sessionId))
        {
            await cache.SetStringAsync(IndexKey, JsonSerializer.Serialize(set), Ttl, ct);
        }
    }

    private static string BillKey(Guid dinningSessionId) => $"payments:bill:{dinningSessionId}";
}
