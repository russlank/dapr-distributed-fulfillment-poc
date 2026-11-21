using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fulfillment.CarrierSimulator.Persistence;

/// <summary>
/// CarrierSimDb, schema <c>carrier</c>: the simulated carrier's own records. Accepted shipments and their callback
/// schedule are committed before the carrier answers; callbacks are sent from these rows and every send is logged.
/// The model exists for migrations; the simulator runs raw SQL.
/// </summary>
public sealed class CarrierDbContext(DbContextOptions<CarrierDbContext> options) : DbContext(options)
{
    public const string Schema = "carrier";

    public DbSet<CarrierShipment> Shipments => Set<CarrierShipment>();

    public DbSet<CarrierCallbackRow> Callbacks => Set<CarrierCallbackRow>();

    public DbSet<CarrierSend> Sends => Set<CarrierSend>();

    public DbSet<CarrierBehavior> Behaviors => Set<CarrierBehavior>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<CarrierShipment>(s =>
        {
            s.ToTable("Shipment");
            s.HasKey(x => x.ShipmentId);
            s.Property(x => x.PayloadHash).HasMaxLength(32).IsFixedLength();
            s.Property(x => x.AcceptedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        });

        modelBuilder.Entity<CarrierCallbackRow>(c =>
        {
            c.ToTable("Callback", t =>
            {
                t.HasCheckConstraint("CK_Callback_State", "State IN ('Scheduled', 'Sent', 'DeadLettered')");
                t.HasCheckConstraint("CK_Callback_Status", "Status IN ('Accepted', 'PickedUp', 'InTransit', 'Delivered', 'Exception')");
            });
            c.HasKey(x => x.Id);
            c.Property(x => x.Status).HasMaxLength(16).IsUnicode(false);
            c.Property(x => x.State).HasMaxLength(16).IsUnicode(false);
            c.Property(x => x.Label).HasMaxLength(80).IsUnicode(false);
            c.Property(x => x.OccurredAt).HasColumnType("datetime2(3)");
            c.Property(x => x.DueAt).HasColumnType("datetime2(3)");
            c.HasIndex(x => new { x.State, x.DueAt });
            c.HasIndex(x => x.ShipmentId);
        });

        modelBuilder.Entity<CarrierSend>(s =>
        {
            s.ToTable("Send");
            s.HasKey(x => x.Id);
            s.Property(x => x.Target).HasMaxLength(200).IsUnicode(false);
            s.Property(x => x.Detail).HasMaxLength(400);
            s.Property(x => x.SentAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            s.HasIndex(x => x.CallbackId);
        });

        modelBuilder.Entity<CarrierBehavior>(b =>
        {
            b.ToTable("Behavior", t => t.HasCheckConstraint("CK_Behavior_Mode", "Mode IN ('Auto', 'Manual', 'Lost', 'LostNoPoll')"));
            b.HasKey(x => x.ShipmentId);
            b.Property(x => x.Mode).HasMaxLength(16).IsUnicode(false);
        });
    }
}

/// <summary>An accepted shipment; <see cref="ShipmentId"/> is the shipper's idempotency key.</summary>
public sealed class CarrierShipment
{
    public Guid ShipmentId { get; private set; }

    public Guid OrderId { get; private set; }

    public int Parcels { get; private set; }

    public byte[] PayloadHash { get; private set; } = [];

    public DateTime AcceptedAt { get; private set; }

    public int Replays { get; private set; }
}

/// <summary>
/// A callback to send. Normally one per carrier transition (<see cref="IsTruth"/>: it is the carrier's record of the
/// shipment); drill scripts add anomalies (repeats, a wrong order, a bad signature, another payload under an existing
/// event id) that are sent but are not the carrier's record.
/// </summary>
public sealed class CarrierCallbackRow
{
    public long Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid ShipmentId { get; private set; }

    /// <summary>The order id sent in the callback (a drill can send a wrong one).</summary>
    public Guid OrderId { get; private set; }

    public string Status { get; private set; } = "";

    public int Version { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public DateTime DueAt { get; private set; }

    public string State { get; private set; } = "";

    public int Attempts { get; private set; }

    public int? LastResponse { get; private set; }

    public bool IsTruth { get; private set; }

    public bool BadSignature { get; private set; }

    /// <summary>Drill label (cleanup and evidence).</summary>
    public string? Label { get; private set; }
}

/// <summary>One send attempt of a callback and the receiver's answer.</summary>
public sealed class CarrierSend
{
    public long Id { get; private set; }

    public long CallbackId { get; private set; }

    public Guid EventId { get; private set; }

    public int Attempt { get; private set; }

    public string Target { get; private set; } = "";

    public int? ResponseCode { get; private set; }

    public string? Detail { get; private set; }

    public DateTime SentAt { get; private set; }
}

/// <summary>Drill setting per shipment id (may be set before the shipment exists): Manual sends only scripted callbacks.</summary>
public sealed class CarrierBehavior
{
    public Guid ShipmentId { get; private set; }

    public string Mode { get; private set; } = "";
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no connection is opened).</summary>
public sealed class CarrierDbDesignTimeFactory : IDesignTimeDbContextFactory<CarrierDbContext>
{
    public CarrierDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<CarrierDbContext>();
        CarrierDbContext.Configure(builder, "Server=design-time;Database=CarrierSimDb;Integrated Security=false");
        return new CarrierDbContext(builder.Options);
    }
}
