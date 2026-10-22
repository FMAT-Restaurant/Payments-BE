using Microsoft.EntityFrameworkCore;
using PaymentsModule.Data;
using PaymentsModule.Models;

namespace PaymentsModule.Tests;

// The read-side tests share one database, so they run sequentially in a single collection.
[CollectionDefinition(QueriesDatabase.CollectionName)]
public class QueriesDatabaseCollection;

// Real SQL Server like ChargeServiceTests, but a database of its own so both suites can run in parallel.
public static class QueriesDatabase
{
    public const string CollectionName = "queries-database";

    private static readonly string ConnectionString =
        (Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer")
        ?? "Server=localhost,14330;Database=payments_tests;User Id=sa;Password=Payments_Dev_2026!;TrustServerCertificate=True")
        .Replace("Database=payments_tests;", "Database=payments_tests_queries;", StringComparison.Ordinal);

    private static long _number;

    public static PaymentsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlServer(ConnectionString).Options);

    public static async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    /// <summary>Seeds a completed-by-default transaction with one dish; payments default to 500.00 in cash.</summary>
    public static async Task<PaymentTransaction> SeedAsync(
        string folio,
        DateTimeOffset createdAt,
        TransactionStatus status = TransactionStatus.Completed,
        params (PaymentMethod Method, decimal Amount)[] payments)
    {
        if (payments.Length == 0)
        {
            payments = [(PaymentMethod.Cash, 500m)];
        }

        var total = payments.Sum(p => p.Amount);
        await using var db = CreateContext();
        var shift = new CashShift { CashierId = "cashier-1", InitialFund = 500m };
        db.CashShifts.Add(shift);

        var transaction = new PaymentTransaction
        {
            Number = Interlocked.Increment(ref _number),
            Folio = folio,
            DinningSessionId = Guid.NewGuid(),
            CashShiftId = shift.Id,
            CashierId = shift.CashierId,
            Subtotal = total,
            Total = total,
            Status = status,
            CreatedAt = createdAt,
            Items = [new TransactionItem { DishName = "Ribeye", Quantity = 1, UnitPrice = total, Amount = total }],
            Payments = payments
                .Select(p => new PaymentBreakdown
                {
                    Method = p.Method,
                    Amount = p.Amount,
                    AuthorizationReference = p.Method == PaymentMethod.Cash ? null : "AUTH-123456"
                })
                .ToList()
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction;
    }
}
