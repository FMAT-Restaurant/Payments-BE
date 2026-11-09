using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using PaymentsModule.Models;
using PaymentsModule.Services;

namespace PaymentsModule.Tests;

public class PendingBillsHttpTests(PaymentsApiFactory api) : IClassFixture<PaymentsApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => PaymentsApiFactory.EnsureDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // RBAC: Request without token -> 401 Unauthorized.
    [Fact]
    public async Task Request_without_token_returns_401()
    {
        using var client = api.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/payments/pendings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // RBAC: Request with unauthorized role (e.g. Mesero) -> 403 Forbidden.
    [Fact]
    public async Task Request_with_unauthorized_role_returns_403()
    {
        using var client = api.CreateAuthenticatedClient("waiter-test", "Mesero");

        var response = await client.GetAsync("/api/v1/payments/pendings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Scenario 2: No hay cuentas pendientes de cobro -> 200 OK con arreglo vacío.
    [Fact]
    public async Task List_when_no_pending_bills_returns_empty_array()
    {
        await ClearPendingIndexAsync();
        using var client = api.CreateAuthenticatedClient("cashier-test", "Cajero");

        var response = await client.GetAsync("/api/v1/payments/pendings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<PendingBillResponse>>();
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    // Scenario 1: Existen cuentas pendientes en Redis (Listado general) -> 200 OK con arreglo de cuentas en snake_case.
    [Fact]
    public async Task List_with_pending_bills_returns_all_active_accounts_in_snake_case()
    {
        await ClearPendingIndexAsync();
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        await SeedPendingBillAsync(session1, "#ORD-9938", "Mesa 04", "Carlos R.", 240.00m);
        await SeedPendingBillAsync(session2, "#ORD-9939", "Mesa 05", "Ana P.", 350.00m);

        using var client = api.CreateAuthenticatedClient("cashier-test", "Cajero");

        var response = await client.GetAsync("/api/v1/payments/pendings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"dinning_session_id\"", rawJson);
        Assert.Contains("\"order_folio\"", rawJson);
        Assert.Contains("\"table_name\"", rawJson);
        Assert.Contains("\"waiter_name\"", rawJson);
        Assert.Contains("\"subtotal\"", rawJson);
        Assert.Contains("\"taxes\"", rawJson);
        Assert.Contains("\"total\"", rawJson);

        var list = await response.Content.ReadFromJsonAsync<List<PendingBillResponse>>();
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, b => b.DinningSessionId == session1 && b.OrderFolio == "#ORD-9938" && b.TableName == "Mesa 04");
        Assert.Contains(list, b => b.DinningSessionId == session2 && b.OrderFolio == "#ORD-9939" && b.TableName == "Mesa 05");
    }

    // Scenario 3: Recuperación de una cuenta específica al recargar la vista (F5) -> 200 OK con el objeto.
    [Fact]
    public async Task Get_by_id_returns_single_pending_bill_for_rehydration()
    {
        var session = Guid.NewGuid();
        await SeedPendingBillAsync(session, "#ORD-9938", "Mesa 04", "Carlos R.", 240.00m);

        using var client = api.CreateAuthenticatedClient("cashier-test", "Cajero");

        var response = await client.GetAsync($"/api/v1/payments/pendings/{session}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bill = await response.Content.ReadFromJsonAsync<PendingBillResponse>();
        Assert.NotNull(bill);
        Assert.Equal(session, bill.DinningSessionId);
        Assert.Equal("#ORD-9938", bill.OrderFolio);
        Assert.Equal("Mesa 04", bill.TableName);
        Assert.Equal("Carlos R.", bill.WaiterName);
        Assert.Equal(240.00m, bill.Total);
    }

    // Scenario: Acceso a una cuenta específica que ya fue cobrada o no existe -> 404 SESSION_NOT_FOUND.
    [Fact]
    public async Task Get_by_id_for_non_existent_or_already_paid_account_returns_404_session_not_found()
    {
        var missingSession = Guid.NewGuid();
        using var client = api.CreateAuthenticatedClient("cashier-test", "Cajero");

        var response = await client.GetAsync($"/api/v1/payments/pendings/{missingSession}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("SESSION_NOT_FOUND", error.Code);
        Assert.Equal("La cuenta solicitada no existe o ya fue procesada.", error.Message);
    }

    // Input validation: formato inválido de dinning_session_id -> 400 VALIDATION_ERROR.
    [Theory]
    [InlineData("abc")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Get_by_id_with_invalid_guid_returns_400_validation_error(string invalidId)
    {
        using var client = api.CreateAuthenticatedClient("cashier-test", "Cajero");

        var response = await client.GetAsync($"/api/v1/payments/pendings/{invalidId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal(ErrorResponse.ValidationErrorCode, error.Code);
        Assert.Equal("El id de sesión tiene un formato inválido.", error.Message);
    }

    // Scenario: Falla de conexión con Redis -> 503 REDIS_TIMEOUT.
    [Fact]
    public async Task Redis_failure_returns_503_service_unavailable()
    {
        using var factory = api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IDistributedCache, FailingDistributedCache>();
            });
        });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "cashier-test");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "Cajero");

        var response = await client.GetAsync("/api/v1/payments/pendings");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("REDIS_TIMEOUT", error.Code);
        Assert.Equal("El servicio de caché no se encuentra disponible temporalmente.", error.Message);
    }

    // Allowed roles: Gerente and Administrador can also access.
    [Theory]
    [InlineData("Gerente")]
    [InlineData("Administrador")]
    [InlineData("Admin")]
    public async Task Allowed_supervision_roles_can_access_pendings(string role)
    {
        using var client = api.CreateAuthenticatedClient("supervisor-test", role);

        var response = await client.GetAsync("/api/v1/payments/pendings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SeedPendingBillAsync(Guid session, string orderFolio, string tableName, string waiterName, decimal total)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var bills = scope.ServiceProvider.GetRequiredService<PendingBillService>();

        var bill = new DinningSessionReadyEvent(
            Guid.NewGuid(),
            session,
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            [new BillItem(Guid.NewGuid(), "Tacos al Pastor", 2, total / 2, total, "Sin cebolla")],
            new BillTotals(total * 0.86m, total * 0.14m, total),
            TableName: tableName,
            OrderFolio: orderFolio,
            WaiterName: waiterName);

        await bills.SaveSnapshotAsync(bill);
    }

    private async Task ClearPendingIndexAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        await cache.RemoveAsync(PendingBillService.IndexKey);
    }

    private sealed class FailingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new TimeoutException("Redis timeout simulated.");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis timeout simulated.");
        public void Refresh(string key) => throw new TimeoutException("Redis timeout simulated.");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis timeout simulated.");
        public void Remove(string key) => throw new TimeoutException("Redis timeout simulated.");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new TimeoutException("Redis timeout simulated.");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new TimeoutException("Redis timeout simulated.");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new TimeoutException("Redis timeout simulated.");
    }
}
