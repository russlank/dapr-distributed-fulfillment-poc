using Fulfillment.Contracts.Events;
using Fulfillment.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fulfillment.PaymentService.Persistence;

/// <summary>
/// PaymentDb, schema <c>payment</c>: one row per payment operation key (the intent, written before the provider
/// is called, and its outcome) and one row per request or status query sent for it. The model exists for
/// migrations; the payment executor runs raw SQL.
/// </summary>
public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public const string Schema = "payment";

    public DbSet<PaymentOperation> Operations => Set<PaymentOperation>();

    public DbSet<PaymentAttempt> Attempts => Set<PaymentAttempt>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        OutboxModel.Configure(modelBuilder, [EventTypes.PaymentOperationRecorded]);

        modelBuilder.Entity<PaymentOperation>(op =>
        {
            op.ToTable("PaymentOperation", t =>
            {
                t.HasCheckConstraint("CK_PaymentOperation_Kind", "Kind IN ('Authorize', 'Capture', 'Void', 'Refund')");
                t.HasCheckConstraint("CK_PaymentOperation_State", "State IN ('Pending', 'Finished')");
                t.HasCheckConstraint("CK_PaymentOperation_Outcome",
                    "Outcome IS NULL OR Outcome IN ('Authorized', 'Declined', 'Captured', 'Voided', 'Refunded', 'NothingToCompensate', 'Rejected', 'Prohibited', 'Unavailable', 'Ambiguous')");
                // Finished exactly when an outcome is recorded.
                t.HasCheckConstraint("CK_PaymentOperation_Finished", "(State = 'Finished' AND Outcome IS NOT NULL AND FinishedAt IS NOT NULL) OR (State = 'Pending' AND Outcome IS NULL AND FinishedAt IS NULL)");
                t.HasCheckConstraint("CK_PaymentOperation_Amount", "AmountMinor >= 0");
            });
            op.HasKey(o => o.OperationKey);
            op.Property(o => o.OperationKey).HasMaxLength(200).IsUnicode(false);
            op.Property(o => o.Kind).HasMaxLength(16).IsUnicode(false);
            op.Property(o => o.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            op.Property(o => o.Reference).HasMaxLength(200).IsUnicode(false);
            op.Property(o => o.State).HasMaxLength(16).IsUnicode(false);
            op.Property(o => o.Outcome).HasMaxLength(24).IsUnicode(false);
            op.Property(o => o.ResultCode).HasMaxLength(40).IsUnicode(false);
            op.Property(o => o.FinishedBy).HasMaxLength(80).IsUnicode(false);
            op.Property(o => o.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            op.Property(o => o.FinishedAt).HasColumnType("datetime2(3)");
            op.Property(o => o.AuthorizationExpiresAt).HasColumnType("datetime2(3)");
            op.HasIndex(o => o.OrderId);
        });

        modelBuilder.Entity<PaymentAttempt>(a =>
        {
            a.ToTable("PaymentAttempt", t => t.HasCheckConstraint("CK_PaymentAttempt_Kind", "Kind IN ('Send', 'Status')"));
            a.HasKey(x => x.Id);
            a.Property(x => x.OperationKey).HasMaxLength(200).IsUnicode(false);
            a.Property(x => x.Kind).HasMaxLength(8).IsUnicode(false);
            a.Property(x => x.Answer).HasMaxLength(24).IsUnicode(false);
            a.Property(x => x.Detail).HasMaxLength(400);
            a.Property(x => x.Replica).HasMaxLength(80).IsUnicode(false);
            a.Property(x => x.StartedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            a.Property(x => x.EndedAt).HasColumnType("datetime2(3)");
            a.HasOne<PaymentOperation>().WithMany().HasForeignKey(x => x.OperationKey);
            a.HasIndex(x => x.OperationKey);
        });
    }
}

/// <summary>A payment operation: written before the first request, finished once with its outcome.</summary>
public sealed class PaymentOperation
{
    public string OperationKey { get; private set; } = "";

    public Guid OrderId { get; private set; }

    public string Kind { get; private set; } = "";

    public long AmountMinor { get; private set; }

    public string Currency { get; private set; } = "";

    /// <summary>The operation key this one acts on (the authorization or the capture).</summary>
    public string? Reference { get; private set; }

    public string State { get; private set; } = "";

    public string? Outcome { get; private set; }

    public string? ResultCode { get; private set; }

    public DateTime? AuthorizationExpiresAt { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? FinishedAt { get; private set; }

    public string? FinishedBy { get; private set; }

    /// <summary>Executions that found the operation already finished and returned the stored outcome.</summary>
    public int Replays { get; private set; }
}

/// <summary>
/// One request (Send) or status query (Status) for an operation. A Send row is written before the request; a
/// Send without an answer is a request that may have reached the provider.
/// </summary>
public sealed class PaymentAttempt
{
    public long Id { get; private set; }

    public string OperationKey { get; private set; } = "";

    public string Kind { get; private set; } = "";

    /// <summary>Committed, Prohibited, NotFound, Unknown, Refused (nothing sent), Rejected503, Timeout, Error, Conflict; null: no answer recorded.</summary>
    public string? Answer { get; private set; }

    public int? HttpStatus { get; private set; }

    public string? Detail { get; private set; }

    public string Replica { get; private set; } = "";

    public DateTime StartedAt { get; private set; }

    public DateTime? EndedAt { get; private set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no connection is opened).</summary>
public sealed class PaymentDbDesignTimeFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<PaymentDbContext>();
        PaymentDbContext.Configure(builder, "Server=design-time;Database=PaymentDb;Integrated Security=false");
        return new PaymentDbContext(builder.Options);
    }
}
