using System.Collections.Concurrent;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

/// <summary>Stands in for the Redis sorted set, since the test suites (and CI) run without Redis.</summary>
public sealed class InMemoryPendingSessionIndex : IPendingSessionIndex
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _sessions = new();

    public bool Contains(Guid dinningSessionId) => _sessions.ContainsKey(dinningSessionId);

    public Task AddAsync(Guid dinningSessionId, DateTimeOffset updatedAt)
    {
        _sessions[dinningSessionId] = updatedAt;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid dinningSessionId)
    {
        _sessions.TryRemove(dinningSessionId, out _);
        return Task.CompletedTask;
    }
}
