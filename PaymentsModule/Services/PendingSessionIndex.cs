using StackExchange.Redis;

namespace PaymentsModule.Services;

/// <summary>
/// Index of the dinning sessions waiting to be charged (PMC-5 AC3), so PMC-26/PMC-27 can list them without <c>KEYS *</c>.
/// </summary>
public interface IPendingSessionIndex
{
    /// <summary>Adds the session, or moves it to <paramref name="updatedAt"/> if it was already pending.</summary>
    Task AddAsync(Guid dinningSessionId, DateTimeOffset updatedAt);

    /// <summary>Removes the session once it is charged (PMC-6) or cancelled (PMC-28).</summary>
    Task RemoveAsync(Guid dinningSessionId);
}

/// <summary>
/// Redis sorted set scored by the last update in Unix milliseconds, so PMC-27 can answer <c>?since=</c> with <c>ZRANGEBYSCORE</c>.
/// </summary>
public class RedisPendingSessionIndex(IConnectionMultiplexer redis) : IPendingSessionIndex
{
    public const string Key = "payments:pending_sessions";

    public async Task AddAsync(Guid dinningSessionId, DateTimeOffset updatedAt)
    {
        var db = redis.GetDatabase();
        await db.SortedSetAddAsync(Key, dinningSessionId.ToString(), updatedAt.ToUnixTimeMilliseconds());
        // Bills expire after 24 h (PendingBillService) but set members do not: drop the ones no bill backs anymore.
        await db.SortedSetRemoveRangeByScoreAsync(Key, double.NegativeInfinity, updatedAt.AddHours(-24).ToUnixTimeMilliseconds());
    }

    public Task RemoveAsync(Guid dinningSessionId) =>
        redis.GetDatabase().SortedSetRemoveAsync(Key, dinningSessionId.ToString());
}
