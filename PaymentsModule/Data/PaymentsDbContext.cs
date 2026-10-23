using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentsModule.Models;

namespace PaymentsModule.Data;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string FolioSequence = "transaction_number_seq";

    public DbSet<CashShift> CashShifts => Set<CashShift>();
    public DbSet<PaymentTransaction> Transactions => Set<PaymentTransaction>();
    public DbSet<SaleCancellation> SaleCancellations => Set<SaleCancellation>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(18, 2);
        builder.Properties<CashShiftStatus>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<TransactionStatus>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<PaymentMethod>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(FolioSequence);

        modelBuilder.Entity<CashShift>(e =>
        {
            e.Property(s => s.CashierId).HasMaxLength(128);
            e.Property(s => s.RowVersion).IsRowVersion();
            // Concurrency guard: at most one open shift per cashier at any time.
            e.HasIndex(s => s.CashierId)
                .IsUnique()
                .HasFilter("[Status] = 'Open'");
        });

        modelBuilder.Entity<PaymentTransaction>(e =>
        {
            e.Property(t => t.Folio).HasMaxLength(32);
            e.Property(t => t.CashierId).HasMaxLength(128);
            e.HasIndex(t => t.Folio).IsUnique();
            e.HasIndex(t => t.Number).IsUnique();
            // Idempotency guard: a session can be charged once, even under concurrent retries.
            e.HasIndex(t => t.DinningSessionId).IsUnique();
            e.HasOne<CashShift>().WithMany().HasForeignKey(t => t.CashShiftId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(t => t.Items).WithOne().HasForeignKey(i => i.TransactionId);
            e.HasMany(t => t.Payments).WithOne().HasForeignKey(p => p.TransactionId);
        });

        modelBuilder.Entity<SaleCancellation>(e =>
        {
            e.Property(c => c.AuthorizerId).HasMaxLength(128);
            e.Property(c => c.Reason).HasMaxLength(500);
            e.Property(c => c.GatewayReference).HasMaxLength(500);
            // Idempotency guard: a sale is cancelled once, even under concurrent requests.
            e.HasIndex(c => c.TransactionId).IsUnique();
            e.HasOne<PaymentTransaction>().WithMany().HasForeignKey(c => c.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransactionItem>().Property(i => i.DishName).HasMaxLength(200);
        modelBuilder.Entity<PaymentBreakdown>().Property(p => p.AuthorizationReference).HasMaxLength(100);

        // MassTransit transactional outbox tables.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
