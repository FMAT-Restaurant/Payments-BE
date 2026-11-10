using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

public class PendingBillServiceTests
{
    private readonly IDistributedCache _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly InMemoryPendingSessionIndex _index = new();
    private readonly PendingBillService _service;

    public PendingBillServiceTests()
    {
        _service = new PendingBillService(_cache, _index);
    }

    [Fact]
    public async Task GetAllPendingAsync_when_empty_returns_empty_list()
    {
        var result = await _service.GetAllPendingAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SaveSnapshotAsync_adds_to_active_index_and_retrieves_all()
    {
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        var bill1 = CreateEvent(session1, "#ORD-0001", "Mesa 01", "Carlos R.", 150m);
        var bill2 = CreateEvent(session2, "#ORD-0002", "Mesa 02", "Ana P.", 280m);

        await _service.SaveSnapshotAsync(bill1);
        await _service.SaveSnapshotAsync(bill2);

        var list = await _service.GetAllPendingAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, b => b.DinningSessionId == session1 && b.OrderFolio == "#ORD-0001" && b.TableName == "Mesa 01");
        Assert.Contains(list, b => b.DinningSessionId == session2 && b.OrderFolio == "#ORD-0002" && b.TableName == "Mesa 02");
    }

    [Fact]
    public async Task GetPendingBillAsync_returns_mapped_dto_with_all_fields()
    {
        var session = Guid.NewGuid();
        var bill = new DinningSessionReadyEvent(
            Guid.NewGuid(),
            session,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new List<BillItem>
            {
                new(Guid.NewGuid(), "Tacos al Pastor", 2, 60.00m, 120.00m, "Sin cebolla"),
                new(Guid.NewGuid(), "Agua de Horchata", 1, 35.00m, 35.00m, null)
            },
            new BillTotals(133.62m, 21.38m, 155.00m),
            TableName: "Mesa 04",
            OrderFolio: "#ORD-9938",
            WaiterName: "Carlos R.");

        await _service.SaveSnapshotAsync(bill);

        var pending = await _service.GetPendingBillAsync(session);

        Assert.NotNull(pending);
        Assert.Equal(session, pending.DinningSessionId);
        Assert.Equal("#ORD-9938", pending.OrderFolio);
        Assert.Equal("Mesa 04", pending.TableName);
        Assert.Equal("Carlos R.", pending.WaiterName);
        Assert.Equal(133.62m, pending.Subtotal);
        Assert.Equal(21.38m, pending.Taxes);
        Assert.Equal(155.00m, pending.Total);
        Assert.Equal(2, pending.Items.Count);

        var itemWithNotes = pending.Items[0];
        Assert.Equal("Tacos al Pastor", itemWithNotes.Name);
        Assert.Equal(2, itemWithNotes.Quantity);
        Assert.Equal(60.00m, itemWithNotes.UnitPrice);
        Assert.Equal(120.00m, itemWithNotes.Amount);
        Assert.Equal("Sin cebolla", itemWithNotes.Notes);
    }

    [Fact]
    public async Task GetPendingBillAsync_for_non_existent_session_returns_null()
    {
        var result = await _service.GetPendingBillAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_removes_bill_from_cache_and_from_index()
    {
        var session = Guid.NewGuid();
        var bill = CreateEvent(session, "#ORD-100", "Mesa 10", "Juan", 200m);

        await _service.SaveSnapshotAsync(bill);
        Assert.NotNull(await _service.GetPendingBillAsync(session));

        await _service.RemoveAsync(session);

        Assert.Null(await _service.GetPendingBillAsync(session));
        var remaining = await _service.GetAllPendingAsync();
        Assert.DoesNotContain(remaining, b => b.DinningSessionId == session);
    }

    [Fact]
    public async Task Older_event_does_not_overwrite_newer_snapshot()
    {
        var session = Guid.NewGuid();
        var newerTime = DateTimeOffset.UtcNow;
        var olderTime = newerTime.AddMinutes(-10);

        var newer = new DinningSessionReadyEvent(
            Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), newerTime,
            [new BillItem(Guid.NewGuid(), "Platillo Nuevo", 1, 100m, 100m)],
            new BillTotals(86.21m, 13.79m, 100m), "Mesa 1", "#ORD-1", "Mozo");

        var older = new DinningSessionReadyEvent(
            Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), olderTime,
            [new BillItem(Guid.NewGuid(), "Platillo Viejo", 1, 50m, 50m)],
            new BillTotals(43.10m, 6.90m, 50m), "Mesa 1", "#ORD-1", "Mozo");

        Assert.True(await _service.SaveSnapshotAsync(newer));
        Assert.False(await _service.SaveSnapshotAsync(older));

        var current = await _service.GetPendingBillAsync(session);
        Assert.NotNull(current);
        Assert.Equal(100m, current.Total);
    }

    [Fact]
    public async Task GetAllPendingAsync_with_since_parameter_filters_sessions_by_timestamp()
    {
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        var bill1 = new DinningSessionReadyEvent(
            Guid.NewGuid(), session1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-20),
            [new BillItem(Guid.NewGuid(), "Platillo 1", 1, 100m, 100m)],
            new BillTotals(86.21m, 13.79m, 100m), "Mesa 1", "#ORD-1", "Mozo");

        var bill2 = new DinningSessionReadyEvent(
            Guid.NewGuid(), session2, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new BillItem(Guid.NewGuid(), "Platillo 2", 1, 200m, 200m)],
            new BillTotals(172.41m, 27.59m, 200m), "Mesa 2", "#ORD-2", "Mozo");

        await _service.SaveSnapshotAsync(bill1);
        // Backdate session1 in the index to simulate arrival before the cutoff
        await _index.AddAsync(session1, DateTimeOffset.UtcNow.AddMinutes(-20));

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);

        await _service.SaveSnapshotAsync(bill2);

        // Filter with since = cutoff -> only bill2 received after cutoff is returned
        var filtered = await _service.GetAllPendingAsync(cutoff);
        Assert.Single(filtered);
        Assert.Equal(session2, filtered[0].DinningSessionId);

        // Query with since beyond all bills -> empty list
        var none = await _service.GetAllPendingAsync(DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Empty(none);

        // Query without since -> returns both bills
        var all = await _service.GetAllPendingAsync();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task SaveSnapshotAsync_scores_session_by_received_time_not_event_timestamp()
    {
        var session = Guid.NewGuid();
        var oldEventTime = DateTimeOffset.UtcNow.AddHours(-2);
        var bill = new DinningSessionReadyEvent(
            Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), oldEventTime,
            [new BillItem(Guid.NewGuid(), "Platillo", 1, 100m, 100m)],
            new BillTotals(86.21m, 13.79m, 100m), "Mesa 1", "#ORD-1", "Mozo");

        var beforeSave = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _service.SaveSnapshotAsync(bill);

        // A query with since right before save MUST return the session,
        // even though the event timestamp was 2 hours ago (hallazgo 2).
        var returned = await _service.GetAllPendingAsync(since: beforeSave);
        Assert.Single(returned);
        Assert.Equal(session, returned[0].DinningSessionId);
    }

    [Fact]
    public void FromEvent_maps_empty_or_whitespace_display_fields_to_null()
    {
        var session = Guid.NewGuid();
        var bill = new DinningSessionReadyEvent(
            Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new BillItem(Guid.NewGuid(), "Platillo", 1, 100m, 100m)],
            new BillTotals(86.21m, 13.79m, 100m),
            TableName: "  ",
            OrderFolio: null,
            WaiterName: "");

        var response = PendingBillResponse.FromEvent(bill);

        Assert.Null(response.OrderFolio);
        Assert.Null(response.TableName);
        Assert.Null(response.WaiterName);
    }

    [Fact]
    public async Task SaveSnapshotAsync_discards_late_event_if_session_already_paid()
    {
        var session = Guid.NewGuid();
        var bill = CreateEvent(session, "#ORD-1", "Mesa 1", "Mozo", 100m);

        // Initial snapshot arrives and is saved
        Assert.True(await _service.SaveSnapshotAsync(bill));

        // The session is paid and marked closed
        await _service.MarkPaidAsync(session);

        // A late event arrives for the paid session
        var lateEvent = new DinningSessionReadyEvent(
            Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            [new BillItem(Guid.NewGuid(), "Platillo", 1, 100m, 100m)],
            new BillTotals(86.21m, 13.79m, 100m), "Mesa 1", "#ORD-1", "Mozo");

        var saved = await _service.SaveSnapshotAsync(lateEvent);

        Assert.False(saved);
        Assert.Null(await _service.GetPendingBillAsync(session));
        var remaining = await _service.GetAllPendingAsync();
        Assert.DoesNotContain(remaining, b => b.DinningSessionId == session);
    }

    private static DinningSessionReadyEvent CreateEvent(Guid session, string orderFolio, string tableName, string waiterName, decimal total) =>
        new(
            Guid.NewGuid(),
            session,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            [new BillItem(Guid.NewGuid(), "Platillo", 1, total, total)],
            new BillTotals(total * 0.86m, total * 0.14m, total),
            TableName: tableName,
            OrderFolio: orderFolio,
            WaiterName: waiterName);
}
