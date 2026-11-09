using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

public class PendingBillServiceTests
{
    private readonly IDistributedCache _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly PendingBillService _service;

    public PendingBillServiceTests()
    {
        _service = new PendingBillService(_cache);
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
