using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fulfillment.ShippingService.Persistence;

/// <summary>
/// ShippingDb, schema <c>shipping</c>: the authority for shipment state and the carrier status history. The model
/// exists for migrations; the operations run raw SQL.
/// </summary>
public sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public const string Schema = "shipping";

    public DbSet<Shipment> Shipments => Set<Shipment>();

    public DbSet<CarrierStatusEvent> StatusEvents => Set<CarrierStatusEvent>();

    public DbSet<DispatchAttempt> DispatchAttempts => Set<DispatchAttempt>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        const string statuses = "('Accepted', 'PickedUp', 'InTransit', 'Delivered', 'Exception')";

        modelBuilder.Entity<Shipment>(s =>
        {
            s.ToTable("Shipment", t =>
            {
                t.HasCheckConstraint("CK_Shipment_State", "State IN ('Prepared', 'Dispatched', 'Ambiguous')");
                t.HasCheckConstraint("CK_Shipment_CurrentStatus", $"CurrentStatus IS NULL OR CurrentStatus IN {statuses}");
                t.HasCheckConstraint("CK_Shipment_TerminalStatus", "TerminalStatus IS NULL OR TerminalStatus IN ('Delivered', 'Exception')");
                // A terminal status is recorded with the event that set it, and only then.
                t.HasCheckConstraint("CK_Shipment_Terminal", "(TerminalStatus IS NULL AND TerminalEventId IS NULL) OR (TerminalStatus IS NOT NULL AND TerminalEventId IS NOT NULL)");
            });
            s.HasKey(x => x.ShipmentId);
            s.Property(x => x.State).HasMaxLength(16).IsUnicode(false);
            s.Property(x => x.CurrentStatus).HasMaxLength(16).IsUnicode(false);
            s.Property(x => x.TerminalStatus).HasMaxLength(16).IsUnicode(false);
            s.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            s.Property(x => x.CarrierAcceptedAt).HasColumnType("datetime2(3)");
            s.Property(x => x.DeliveryDeadlineAt).HasColumnType("datetime2(3)");
            s.HasIndex(x => x.OrderId).IsUnique();
        });

        modelBuilder.Entity<CarrierStatusEvent>(e =>
        {
            e.ToTable("CarrierStatusEvent", t =>
            {
                t.HasCheckConstraint("CK_CarrierStatusEvent_Status", $"Status IN {statuses}");
                t.HasCheckConstraint("CK_CarrierStatusEvent_Source", "Source IN ('callback', 'poll')");
                t.HasCheckConstraint("CK_CarrierStatusEvent_Outcome",
                    "Outcome IN ('Applied', 'TerminalApplied', 'Stale', 'TerminalConflict', 'TerminalRepeat', 'CorrelationMismatch')");
            });
            e.HasKey(x => x.EventId);
            e.Property(x => x.Status).HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.Source).HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.PayloadHash).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.Outcome).HasMaxLength(24).IsUnicode(false);
            e.Property(x => x.OccurredAt).HasColumnType("datetime2(3)");
            e.Property(x => x.ReceivedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasOne<Shipment>().WithMany().HasForeignKey(x => x.ShipmentId);
            // One event per carrier version of a shipment; a recorded correlation mismatch (an event naming another
            // order) does not occupy the version of the shipment's real event.
            e.HasIndex(x => new { x.ShipmentId, x.Version }).IsUnique().HasFilter("[Outcome] <> 'CorrelationMismatch'")
                .HasDatabaseName("UX_CarrierStatusEvent_ShipmentVersion");
        });

        modelBuilder.Entity<DispatchAttempt>(a =>
        {
            a.ToTable("DispatchAttempt");
            a.HasKey(x => x.Id);
            a.Property(x => x.Answer).HasMaxLength(24).IsUnicode(false);
            a.Property(x => x.Detail).HasMaxLength(400);
            a.Property(x => x.Replica).HasMaxLength(80).IsUnicode(false);
            a.Property(x => x.StartedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            a.HasOne<Shipment>().WithMany().HasForeignKey(x => x.ShipmentId);
        });
    }
}

/// <summary>A shipment; its id is derived from the order (one per order) and is the carrier's idempotency key.</summary>
public sealed class Shipment
{
    public Guid ShipmentId { get; private set; }

    public Guid OrderId { get; private set; }

    public int Parcels { get; private set; }

    /// <summary>Prepared (intent recorded), Dispatched (carrier accepted), Ambiguous (undeterminable).</summary>
    public string State { get; private set; } = "";

    public string? CurrentStatus { get; private set; }

    public int CurrentVersion { get; private set; }

    /// <summary>Once set (Delivered or Exception), never changes.</summary>
    public string? TerminalStatus { get; private set; }

    public Guid? TerminalEventId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? CarrierAcceptedAt { get; private set; }

    /// <summary>SQL time by which a terminal status is expected (dispatch acceptance + the delivery deadline).</summary>
    public DateTime? DeliveryDeadlineAt { get; private set; }
}

/// <summary>Every valid unique carrier status event, in whatever order it arrived, with the outcome it got.</summary>
public sealed class CarrierStatusEvent
{
    public Guid EventId { get; private set; }

    public Guid ShipmentId { get; private set; }

    public Guid OrderId { get; private set; }

    public string Status { get; private set; } = "";

    public int Version { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public string Source { get; private set; } = "";

    public string PayloadHash { get; private set; } = "";

    public string Outcome { get; private set; } = "";

    public DateTime ReceivedAt { get; private set; }

    public int Replays { get; private set; }
}

/// <summary>One shipment request sent to the carrier (written before the request).</summary>
public sealed class DispatchAttempt
{
    public long Id { get; private set; }

    public Guid ShipmentId { get; private set; }

    public string? Answer { get; private set; }

    public int? HttpStatus { get; private set; }

    public string? Detail { get; private set; }

    public string Replica { get; private set; } = "";

    public DateTime StartedAt { get; private set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no connection is opened).</summary>
public sealed class ShippingDbDesignTimeFactory : IDesignTimeDbContextFactory<ShippingDbContext>
{
    public ShippingDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ShippingDbContext>();
        ShippingDbContext.Configure(builder, "Server=design-time;Database=ShippingDb;Integrated Security=false");
        return new ShippingDbContext(builder.Options);
    }
}
