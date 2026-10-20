using MassTransit;
using Microsoft.EntityFrameworkCore;
using PaymentsModule.Models.Entities;

namespace PaymentsModule.Persistence;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public const string IndiceSesionUnica = "UX_TransaccionPago_DinningSessionId";
    public const string IndiceTurnoAbiertoUnico = "UX_TurnoCaja_CajeroId_Abierto";

    public DbSet<TurnoCaja> TurnosCaja => Set<TurnoCaja>();

    public DbSet<TransaccionPago> Transacciones => Set<TransaccionPago>();

    public DbSet<FolioContador> FolioContadores => Set<FolioContador>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TurnoCaja>(e =>
        {
            e.ToTable("TurnoCaja");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CajeroId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.FondoInicial).HasPrecision(18, 2);
            e.Property(x => x.EfectivoEsperado).HasPrecision(18, 2);
            e.Property(x => x.EfectivoDeclarado).HasPrecision(18, 2);
            e.Property(x => x.Diferencia).HasPrecision(18, 2);
            e.Property(x => x.VentasTarjeta).HasPrecision(18, 2);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.CajeroId);

            // Garantía en BD: a lo mucho un turno ABIERTO por cajero (cubre la carrera de doble clic).
            e.HasIndex(x => x.CajeroId)
                .IsUnique()
                .HasFilter("[Estado] = 'ABIERTO'")
                .HasDatabaseName(IndiceTurnoAbiertoUnico);
        });

        modelBuilder.Entity<TransaccionPago>(e =>
        {
            e.ToTable("TransaccionPago");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Folio).HasMaxLength(30).IsRequired();
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Folio).IsUnique();
            e.HasIndex(x => new { x.FechaCreacionUtc, x.Estado });
            e.HasIndex(x => x.DinningSessionId).IsUnique().HasDatabaseName(IndiceSesionUnica);
            e.HasOne<TurnoCaja>().WithMany().HasForeignKey(x => x.TurnoCajaId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Detalles).WithOne().HasForeignKey(x => x.TransaccionPagoId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Desgloses).WithOne().HasForeignKey(x => x.TransaccionPagoId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DetalleTransaccion>(e =>
        {
            e.ToTable("DetalleTransaccion");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.NombrePlatillo).HasMaxLength(200).IsRequired();
            e.Property(x => x.PrecioUnitario).HasPrecision(18, 2);
            e.Property(x => x.Importe).HasPrecision(18, 2);
        });

        modelBuilder.Entity<DesglosePago>(e =>
        {
            e.ToTable("DesglosePago");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.MetodoPago).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Monto).HasPrecision(18, 2);
            e.Property(x => x.ReferenciaAutorizacion).HasMaxLength(100);
        });

        modelBuilder.Entity<FolioContador>(e =>
        {
            e.ToTable("FolioContador");
            e.HasKey(x => x.Fecha);
            e.Property(x => x.Fecha).ValueGeneratedNever();
        });

        // Tablas del Outbox / Inbox de MassTransit (mismo DbContext => mismo COMMIT).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
