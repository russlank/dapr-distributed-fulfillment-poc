using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fulfillment.InventoryService.Persistence;

// Rows of InventoryDb (schema inventory). The model defines the schema and its constraints; every
// change of business state goes through the guarded SQL in InventoryOperations, never through
// tracked entities, so the constraints and conditional updates are the authority.

public sealed class Warehouse
{
    private Warehouse()
    {
    }

    public Warehouse(string warehouseId, string name, int priority) => (WarehouseId, Name, Priority) = (warehouseId, name, priority);

    public string WarehouseId { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>Allocation order: lines are served whole from the first warehouse (lowest number) that can.</summary>
    public int Priority { get; private set; }
}

public sealed class StockItem
{
    private StockItem()
    {
    }

    public StockItem(string warehouseId, string sku, int onHand) => (WarehouseId, Sku, OnHand) = (warehouseId, sku, onHand);

    public string WarehouseId { get; private set; } = "";

    public string Sku { get; private set; } = "";

    public int OnHand { get; private set; }

    /// <summary>Units held by open holds; 0 ≤ Reserved ≤ OnHand (check constraint).</summary>
    public int Reserved { get; private set; }

    public byte[] RowVersion { get; private set; } = [];
}

/// <summary>A reservation of one order line in one warehouse; leaves Held at most once.</summary>
public sealed class Hold
{
    public Guid HoldId { get; private set; }

    public Guid OrderId { get; private set; }

    public int LineNumber { get; private set; }

    public string WarehouseId { get; private set; } = "";

    public string Sku { get; private set; } = "";

    public int Quantity { get; private set; }

    public string State { get; private set; } = "";

    public DateTime CreatedAt { get; private set; }

    public DateTime ExpiresAt { get; private set; }

    public DateTime? ClosedAt { get; private set; }

    /// <summary>The operation key, or sweeper:&lt;replica&gt;, that took the hold out of Held.</summary>
    public string? ClosedBy { get; private set; }

    public string CreatedBy { get; private set; } = "";
}

/// <summary>The result of an operation, keyed by its operation key: a repeat returns it unchanged.</summary>
public sealed class ProcessedOperation
{
    public string OperationKey { get; private set; } = "";

    public string Operation { get; private set; } = "";

    public Guid OrderId { get; private set; }

    public string Outcome { get; private set; } = "";

    public Guid? HoldId { get; private set; }

    public string? WarehouseId { get; private set; }

    public DateTime ProcessedAt { get; private set; }

    public string ProcessedBy { get; private set; } = "";

    /// <summary>How often the same key came again and got this stored result (evidence of retries).</summary>
    public int Replays { get; private set; }
}

/// <summary>Write side of the inventory outbox: one row per hold transition, gapless sequence per order. Publication comes later.</summary>
public sealed class OutboxEntry
{
    public long Id { get; private set; }

    public Guid OrderId { get; private set; }

    public int Sequence { get; private set; }

    public string EventType { get; private set; } = "";

    public Guid HoldId { get; private set; }

    public string Payload { get; private set; } = "";

    public DateTime CreatedAt { get; private set; }

    public string State { get; private set; } = "";
}

/// <summary>An order closed for reservations (its holds were released or consumed); a later reserve is refused.</summary>
public sealed class OrderClosure
{
    public Guid OrderId { get; private set; }

    public string Reason { get; private set; } = "";

    public string OperationKey { get; private set; } = "";

    public DateTime ClosedAt { get; private set; }
}

/// <summary>The sweeper's lease: one replica sweeps at a time; a token fences a stale owner.</summary>
public sealed class SweeperLease
{
    public string Name { get; private set; } = "";

    public string? Owner { get; private set; }

    public Guid? Token { get; private set; }

    public DateTime? ExpiresAt { get; private set; }
}

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : DbContext(options)
{
    public const string Schema = "inventory";

    /// <summary>The catalog seeded into the first warehouse with ample stock, so ordinary orders are served.</summary>
    public static IEnumerable<string> CatalogSkus =>
        Enumerable.Range(1, 100).Select(n => $"SKU-{n}")
            .Concat(Enumerable.Range(1, 50).Select(n => $"SKU-R{n}"))
            .Concat(Enumerable.Range(1, 20).Select(n => $"SKU-X{n}"));

    public const int CatalogStock = 1_000_000;

    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<StockItem> StockItems => Set<StockItem>();

    public DbSet<Hold> Holds => Set<Hold>();

    public DbSet<ProcessedOperation> ProcessedOperations => Set<ProcessedOperation>();

    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();

    public DbSet<OrderClosure> OrderClosures => Set<OrderClosure>();

    public DbSet<SweeperLease> SweeperLeases => Set<SweeperLease>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureWarehouse(modelBuilder.Entity<Warehouse>());
        ConfigureStock(modelBuilder.Entity<StockItem>());
        ConfigureHold(modelBuilder.Entity<Hold>());
        ConfigureProcessed(modelBuilder.Entity<ProcessedOperation>());
        ConfigureOutbox(modelBuilder.Entity<OutboxEntry>());
        ConfigureClosure(modelBuilder.Entity<OrderClosure>());
        ConfigureLease(modelBuilder.Entity<SweeperLease>());
    }

    private static void ConfigureWarehouse(EntityTypeBuilder<Warehouse> warehouse)
    {
        warehouse.ToTable("Warehouse", t => t.HasCheckConstraint("CK_Warehouse_Priority", "[Priority] >= 1"));
        warehouse.HasKey(w => w.WarehouseId);
        warehouse.Property(w => w.WarehouseId).HasMaxLength(16).IsUnicode(false);
        warehouse.Property(w => w.Name).HasMaxLength(64);
        warehouse.HasIndex(w => w.Priority).IsUnique().HasDatabaseName("UX_Warehouse_Priority");
        warehouse.HasData(new Warehouse("W1", "North", 1), new Warehouse("W2", "Central", 2), new Warehouse("W3", "South", 3));
    }

    private static void ConfigureStock(EntityTypeBuilder<StockItem> stock)
    {
        stock.ToTable("StockItem", t =>
        {
            // Invariant I1 (no overselling), enforced by the database whatever the caller does.
            t.HasCheckConstraint("CK_StockItem_OnHand", "[OnHand] >= 0");
            t.HasCheckConstraint("CK_StockItem_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
        });
        stock.HasKey(s => new { s.WarehouseId, s.Sku });
        stock.Property(s => s.WarehouseId).HasMaxLength(16).IsUnicode(false);
        stock.Property(s => s.Sku).HasMaxLength(32).IsUnicode(false);
        stock.Property(s => s.RowVersion).IsRowVersion();
        stock.HasOne<Warehouse>().WithMany().HasForeignKey(s => s.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        stock.HasData(CatalogSkus.Select(sku => new { WarehouseId = "W1", Sku = sku, OnHand = CatalogStock, Reserved = 0 }));
    }

    private static void ConfigureHold(EntityTypeBuilder<Hold> hold)
    {
        hold.ToTable("Hold", t =>
        {
            t.HasCheckConstraint("CK_Hold_Quantity", "[Quantity] > 0");
            t.HasCheckConstraint("CK_Hold_State", "[State] IN ('Held', 'Released', 'Expired', 'Consumed')");
            t.HasCheckConstraint("CK_Hold_Lifecycle",
                "([State] = 'Held' AND [ClosedAt] IS NULL AND [ClosedBy] IS NULL) OR ([State] <> 'Held' AND [ClosedAt] IS NOT NULL AND [ClosedBy] IS NOT NULL)");
            t.HasCheckConstraint("CK_Hold_Expiry", "[ExpiresAt] > [CreatedAt]");
        });
        hold.HasKey(h => h.HoldId);
        hold.Property(h => h.HoldId).HasDefaultValueSql("NEWID()");
        hold.Property(h => h.WarehouseId).HasMaxLength(16).IsUnicode(false);
        hold.Property(h => h.Sku).HasMaxLength(32).IsUnicode(false);
        hold.Property(h => h.State).HasMaxLength(16).IsUnicode(false);
        hold.Property(h => h.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        hold.Property(h => h.ExpiresAt).HasColumnType("datetime2(3)");
        hold.Property(h => h.ClosedAt).HasColumnType("datetime2(3)");
        hold.Property(h => h.ClosedBy).HasMaxLength(200).IsUnicode(false);
        hold.Property(h => h.CreatedBy).HasMaxLength(64).IsUnicode(false);
        // One hold per order line, whatever the warehouse (invariant I2, idempotent reservation).
        hold.HasIndex(h => new { h.OrderId, h.LineNumber }).IsUnique().HasDatabaseName("UX_Hold_Order_Line");
        hold.HasIndex(h => h.ExpiresAt).HasDatabaseName("IX_Hold_Due").HasFilter("[State] = 'Held'");
        hold.HasOne<StockItem>().WithMany().HasForeignKey(h => new { h.WarehouseId, h.Sku }).OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureProcessed(EntityTypeBuilder<ProcessedOperation> processed)
    {
        processed.ToTable("ProcessedOperation", t =>
        {
            t.HasCheckConstraint("CK_ProcessedOperation_Operation", "[Operation] IN ('Reserve', 'Release', 'Consume', 'Extend')");
            t.HasCheckConstraint("CK_ProcessedOperation_Outcome",
                "[Outcome] IN ('Reserved', 'InsufficientStock', 'OrderClosed', 'Released', 'NotHeld', 'Consumed', 'HoldExpired', 'Incomplete', 'Extended', 'InsufficientValidity')");
        });
        processed.HasKey(p => p.OperationKey);
        processed.Property(p => p.OperationKey).HasMaxLength(200).IsUnicode(false);
        processed.Property(p => p.Operation).HasMaxLength(16).IsUnicode(false);
        processed.Property(p => p.Outcome).HasMaxLength(32).IsUnicode(false);
        processed.Property(p => p.WarehouseId).HasMaxLength(16).IsUnicode(false);
        processed.Property(p => p.ProcessedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        processed.Property(p => p.ProcessedBy).HasMaxLength(64).IsUnicode(false);
        processed.HasIndex(p => p.OrderId).HasDatabaseName("IX_ProcessedOperation_Order");
    }

    private static void ConfigureOutbox(EntityTypeBuilder<OutboxEntry> outbox)
    {
        outbox.ToTable("Outbox", t =>
        {
            t.HasCheckConstraint("CK_Outbox_Sequence", "[Sequence] >= 1");
            t.HasCheckConstraint("CK_Outbox_EventType", "[EventType] IN ('HoldCreated', 'HoldReleased', 'HoldExpired', 'HoldConsumed', 'HoldExtended')");
            t.HasCheckConstraint("CK_Outbox_State", "[State] IN ('Pending', 'Published')");
        });
        outbox.HasKey(o => o.Id);
        outbox.Property(o => o.Id).UseIdentityColumn();
        outbox.Property(o => o.EventType).HasMaxLength(32).IsUnicode(false);
        outbox.Property(o => o.Payload).HasMaxLength(2000);
        outbox.Property(o => o.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        outbox.Property(o => o.State).HasMaxLength(16).IsUnicode(false).HasDefaultValue("Pending");
        // Gapless per order: the next sequence is read under a range lock in the same transaction.
        outbox.HasIndex(o => new { o.OrderId, o.Sequence }).IsUnique().HasDatabaseName("UX_Outbox_Order_Sequence");
    }

    private static void ConfigureClosure(EntityTypeBuilder<OrderClosure> closure)
    {
        closure.ToTable("OrderClosure", t => t.HasCheckConstraint("CK_OrderClosure_Reason", "[Reason] IN ('Released', 'Consumed')"));
        closure.HasKey(c => c.OrderId);
        closure.Property(c => c.Reason).HasMaxLength(16).IsUnicode(false);
        closure.Property(c => c.OperationKey).HasMaxLength(200).IsUnicode(false);
        closure.Property(c => c.ClosedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
    }

    private static void ConfigureLease(EntityTypeBuilder<SweeperLease> lease)
    {
        lease.ToTable("SweeperLease", t => t.HasCheckConstraint("CK_SweeperLease_Owner",
            "([Owner] IS NULL AND [Token] IS NULL AND [ExpiresAt] IS NULL) OR ([Owner] IS NOT NULL AND [Token] IS NOT NULL AND [ExpiresAt] IS NOT NULL)"));
        lease.HasKey(l => l.Name);
        lease.Property(l => l.Name).HasMaxLength(64).IsUnicode(false);
        lease.Property(l => l.Owner).HasMaxLength(64).IsUnicode(false);
        lease.Property(l => l.ExpiresAt).HasColumnType("datetime2(3)");
        lease.HasData(new { Name = "hold-expiry" });
    }
}
