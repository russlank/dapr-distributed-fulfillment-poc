using Fulfillment.Contracts.Orders;
using Fulfillment.OrderApi.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fulfillment.OrderApi.Persistence;

/// <summary>
/// Business tables of OrderDb (schema <c>orders</c>), owned by the order API and changed only through
/// migrations. Constraints carry the invariants the code relies on: one start request per order,
/// one order per idempotency key, contiguous status history, bounded lines. The diagnostics schema
/// of the runtime probe is separate and not part of this model.
/// </summary>
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    public DbSet<WorkflowStartRequest> WorkflowStartRequests => Set<WorkflowStartRequest>();

    /// <summary>Applies the SQL Server options shared by the app, the migration job, and the design-time factory.</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureOrder(modelBuilder.Entity<Order>());
        ConfigureLine(modelBuilder.Entity<OrderLine>());
        ConfigureHistory(modelBuilder.Entity<OrderStatusHistory>());
        ConfigureKey(modelBuilder.Entity<IdempotencyKey>());
        ConfigureStartRequest(modelBuilder.Entity<WorkflowStartRequest>());
    }

    private static string InList<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))})";

    private static void ConfigureOrder(EntityTypeBuilder<Order> order)
    {
        order.ToTable("Order", t =>
        {
            t.HasCheckConstraint("CK_Order_Status", InList<OrderStatus>("Status"));
        });
        order.HasKey(o => o.OrderId);
        order.Property(o => o.OrderId).ValueGeneratedNever();
        order.Property(o => o.CustomerId).HasMaxLength(OrderLimits.MaxCustomerIdLength).IsUnicode(false);
        order.Property(o => o.Status).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        order.Property(o => o.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        order.Property(o => o.RowVersion).IsRowVersion();
        order.HasIndex(o => new { o.CustomerId, o.CreatedAt }).HasDatabaseName("IX_Order_Customer");
        order.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        order.HasMany(o => o.History).WithOne().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Cascade);
        order.Navigation(o => o.Lines).HasField("lines").UsePropertyAccessMode(PropertyAccessMode.Field);
        order.Navigation(o => o.History).HasField("history").UsePropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureLine(EntityTypeBuilder<OrderLine> line)
    {
        line.ToTable("OrderLine", t =>
        {
            t.HasCheckConstraint("CK_OrderLine_Quantity", $"[Quantity] BETWEEN {OrderLimits.MinQuantity} AND {OrderLimits.MaxQuantity}");
            t.HasCheckConstraint("CK_OrderLine_LineNumber", $"[LineNumber] BETWEEN 1 AND {OrderLimits.MaxLines}");
        });
        line.HasKey(l => new { l.OrderId, l.LineNumber });
        line.Property(l => l.LineNumber).ValueGeneratedNever();
        line.Property(l => l.Sku).HasMaxLength(OrderLimits.MaxSkuLength).IsUnicode(false);
    }

    private static void ConfigureHistory(EntityTypeBuilder<OrderStatusHistory> history)
    {
        history.ToTable("OrderStatusHistory", t =>
        {
            t.HasCheckConstraint("CK_OrderStatusHistory_ToStatus", InList<OrderStatus>("ToStatus"));
            t.HasCheckConstraint("CK_OrderStatusHistory_Sequence", "[Sequence] >= 1");
        });
        history.HasKey(h => h.Id);
        history.Property(h => h.Id).UseIdentityColumn();
        history.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        history.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        history.Property(h => h.ReasonCode).HasMaxLength(64).IsUnicode(false);
        history.Property(h => h.OccurredAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        history.HasIndex(h => new { h.OrderId, h.Sequence }).IsUnique().HasDatabaseName("UX_OrderStatusHistory_Order_Sequence");
    }

    private static void ConfigureKey(EntityTypeBuilder<IdempotencyKey> key)
    {
        key.ToTable("IdempotencyKey", t =>
        {
            t.HasCheckConstraint("CK_IdempotencyKey_Hash", "DATALENGTH([RequestHash]) = 32");
        });
        key.HasKey(k => k.Key).HasName(OrderDbConstraints.IdempotencyKeyPrimaryKey);
        key.Property(k => k.Key).HasMaxLength(OrderLimits.MaxIdempotencyKeyLength).IsUnicode(false);
        key.Property(k => k.RequestHash).HasColumnType("binary(32)");
        key.Property(k => k.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        key.HasIndex(k => k.OrderId).IsUnique().HasDatabaseName("UX_IdempotencyKey_Order");
        key.HasOne<Order>().WithMany().HasForeignKey(k => k.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureStartRequest(EntityTypeBuilder<WorkflowStartRequest> request)
    {
        request.ToTable("WorkflowStartRequest", t =>
        {
            t.HasCheckConstraint("CK_WorkflowStartRequest_State", InList<WorkflowStartState>("State"));
            t.HasCheckConstraint("CK_WorkflowStartRequest_Counters", "[Attempts] >= 0 AND [ExecutionClaims] >= 0 AND [FenceRejections] >= 0");
            t.HasCheckConstraint("CK_WorkflowStartRequest_Lifecycle",
                "([State] = 'Pending' AND [StartedAt] IS NULL AND [ClosedAt] IS NULL)"
                + " OR ([State] = 'Started' AND [StartedAt] IS NOT NULL AND [ClosedAt] IS NULL)"
                + " OR ([State] = 'Closed' AND [ClosedAt] IS NOT NULL)");
            t.HasCheckConstraint("CK_WorkflowStartRequest_Lease",
                "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL)");
        });
        request.HasKey(r => r.OrderId);
        request.Property(r => r.OrderId).ValueGeneratedNever();
        request.Property(r => r.InstanceId).HasMaxLength(64).IsUnicode(false);
        request.Property(r => r.State).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        request.Property(r => r.NextAttemptAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        request.Property(r => r.LeaseOwner).HasMaxLength(64).IsUnicode(false);
        request.Property(r => r.LeaseExpiresAt).HasColumnType("datetime2(3)");
        request.Property(r => r.LastErrorCode).HasMaxLength(64).IsUnicode(false);
        request.Property(r => r.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        request.Property(r => r.StartedAt).HasColumnType("datetime2(3)");
        request.Property(r => r.ClosedAt).HasColumnType("datetime2(3)");
        request.HasIndex(r => r.InstanceId).IsUnique().HasDatabaseName("UX_WorkflowStartRequest_Instance");
        // The dispatcher's due-work query: Pending rows by due time.
        request.HasIndex(r => r.NextAttemptAt).HasDatabaseName("IX_WorkflowStartRequest_Due")
            .HasFilter("[State] = 'Pending'").IncludeProperties(r => new { r.LeaseExpiresAt });
        request.HasOne<Order>().WithOne().HasForeignKey<WorkflowStartRequest>(r => r.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Constraint names the code matches on (unique-violation handling).</summary>
public static class OrderDbConstraints
{
    public const string IdempotencyKeyPrimaryKey = "PK_IdempotencyKey";
}
