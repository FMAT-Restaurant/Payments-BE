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

    /// <summary>
    /// Saves (or overwrites) the session snapshot. Returns false if the event was already processed or is older than the saved snapshot.
    /// Updates the secondary index in Redis with the session timestamp to support high-performance polling and since filtering (PMC-27).
    /// </summary>
    public async Task<bool> SaveSnapshotAsync(DinningSessionReadyEvent message, CancellationToken ct = default)
    {
        // Out-of-order guard: if the session was already cancelled, discard the late creation event (PMC-28).
        if (await cache.GetAsync(CancelledKey(message.DinningSessionId), ct) is not null)
        {
            return false;
        }

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

        await AddToActiveIndexAsync(message.DinningSessionId, message.Timestamp, ct);
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
    /// Returns active pending bills reading the secondary index in Redis without KEYS * (AC2).
    /// If <paramref name="since"/> is provided, only sessions created or updated on or after that timestamp are returned (PMC-27 AC3).
    /// </summary>
    public async Task<IReadOnlyList<PendingBillResponse>> GetAllPendingAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var indexJson = await cache.GetStringAsync(IndexKey, ct);
        if (string.IsNullOrWhiteSpace(indexJson))
        {
            return [];
        }

        var dict = DeserializeIndex(indexJson);
        if (dict.Count == 0)
        {
            return [];
        }

        var targetKeys = since.HasValue
            ? dict.Where(kvp => kvp.Value >= since.Value).Select(kvp => kvp.Key).ToList()
            : dict.Keys.ToList();

        if (targetKeys.Count == 0)
        {
            return [];
        }

        var list = new List<PendingBillResponse>();
        var expiredOrRemoved = new List<Guid>();

        foreach (var id in targetKeys)
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
                dict.Remove(id);
            }
            await cache.SetStringAsync(IndexKey, JsonSerializer.Serialize(dict), Ttl, ct);
        }

        return list.OrderByDescending(b => b.Timestamp).ToList();
    }

    /// <summary>
    /// Cancels a session when DinningSessionCancelledEvent is received (PMC-28).
    /// Stores a tombstone key to prevent late creation events, deletes the snapshot and purges the active index.
    /// Returns true if the session snapshot existed and was cleaned up, or false if it was not found.
    /// </summary>
    public async Task<bool> CancelSessionAsync(Guid dinningSessionId, CancellationToken ct = default)
    {
        // Store tombstone in Redis with 24h TTL
        await cache.SetStringAsync(CancelledKey(dinningSessionId), "1", Ttl, ct);

        var existing = await GetAsync(dinningSessionId, ct);
        await RemoveAsync(dinningSessionId, ct);

        return existing is not null;
    }

    /// <summary>Removes the session from the cache and from the active index when paid or cancelled (PMC-28 / AC4).</summary>
    public async Task RemoveAsync(Guid dinningSessionId, CancellationToken ct = default)
    {
        await cache.RemoveAsync(BillKey(dinningSessionId), ct);

        var indexJson = await cache.GetStringAsync(IndexKey, ct);
        if (!string.IsNullOrWhiteSpace(indexJson))
        {
            var dict = DeserializeIndex(indexJson);
            if (dict.Remove(dinningSessionId))
            {
                await cache.SetStringAsync(IndexKey, JsonSerializer.Serialize(dict), Ttl, ct);
            }
        }
    }

    private async Task AddToActiveIndexAsync(Guid sessionId, DateTimeOffset updatedAt, CancellationToken ct)
    {
        var indexJson = await cache.GetStringAsync(IndexKey, ct);
        var dict = !string.IsNullOrWhiteSpace(indexJson)
            ? DeserializeIndex(indexJson)
            : [];

        dict[sessionId] = updatedAt;
        await cache.SetStringAsync(IndexKey, JsonSerializer.Serialize(dict), Ttl, ct);
    }

    private static Dictionary<Guid, DateTimeOffset> DeserializeIndex(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<Guid, DateTimeOffset>>(json) ?? [];
        }
        catch
        {
            try
            {
                var set = JsonSerializer.Deserialize<HashSet<Guid>>(json);
                if (set is not null)
                {
                    return set.ToDictionary(id => id, _ => DateTimeOffset.UtcNow);
                }
            }
            catch
            {
            }

            return [];
        }
    }

    private static string BillKey(Guid dinningSessionId) => $"payments:bill:{dinningSessionId}";
    private static string CancelledKey(Guid dinningSessionId) => $"payments:cancelled:{dinningSessionId}";
}
