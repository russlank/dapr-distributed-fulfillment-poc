using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fulfillment.PaymentProviderSimulator.Persistence;

/// <summary>
/// PaymentProviderSimDb, schema <c>provider</c>: the simulated provider's own records. The model exists for
/// migrations; requests run as raw SQL in <see cref="Provider.ProviderOperations"/>.
/// </summary>
public sealed class ProviderDbContext(DbContextOptions<ProviderDbContext> options) : DbContext(options)
{
    public const string Schema = "provider";

    public DbSet<ProviderOperation> Operations => Set<ProviderOperation>();

    public DbSet<ProviderProhibition> Prohibitions => Set<ProviderProhibition>();

    public DbSet<ProviderFault> Faults => Set<ProviderFault>();

    public DbSet<ProviderStatusQuery> StatusQueries => Set<ProviderStatusQuery>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ProviderOperation>(op =>
        {
            op.ToTable("Operation", t =>
            {
                t.HasCheckConstraint("CK_Operation_Kind", "Kind IN ('Authorize', 'Capture', 'Void', 'Refund')");
                t.HasCheckConstraint("CK_Operation_Result",
                    "Result IN ('Authorized', 'Declined', 'Captured', 'Voided', 'Refunded', 'NothingToVoid', 'NothingToRefund', 'Rejected')");
                t.HasCheckConstraint("CK_Operation_Amount", "AmountMinor >= 0");
                // Captures, voids, and refunds name the operation they act on; an authorization names none.
                t.HasCheckConstraint("CK_Operation_Reference", "(Kind = 'Authorize' AND Reference IS NULL) OR (Kind <> 'Authorize' AND Reference IS NOT NULL)");
            });
            op.HasKey(o => o.OperationKey);
            op.Property(o => o.OperationKey).HasMaxLength(200).IsUnicode(false);
            op.Property(o => o.Kind).HasMaxLength(16).IsUnicode(false);
            op.Property(o => o.PayloadHash).HasMaxLength(32).IsFixedLength();
            op.Property(o => o.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
            op.Property(o => o.Reference).HasMaxLength(200).IsUnicode(false);
            op.Property(o => o.Result).HasMaxLength(16).IsUnicode(false);
            op.Property(o => o.ResultCode).HasMaxLength(40).IsUnicode(false);
            op.Property(o => o.FaultApplied).HasMaxLength(60).IsUnicode(false);
            op.Property(o => o.CommittedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            op.Property(o => o.AuthorizationExpiresAt).HasColumnType("datetime2(3)");
            // One effective capture per authorization, one effective void per authorization, one refund per capture.
            op.HasIndex(o => new { o.Reference, o.Kind }).IsUnique()
                .HasFilter("[Reference] IS NOT NULL AND [Result] IN ('Captured', 'Voided', 'Refunded')")
                .HasDatabaseName("UX_Operation_EffectPerReference");
        });

        modelBuilder.Entity<ProviderProhibition>(p =>
        {
            p.ToTable("Prohibition");
            p.HasKey(x => x.OperationKey);
            p.Property(x => x.OperationKey).HasMaxLength(200).IsUnicode(false);
            p.Property(x => x.ByOperationKey).HasMaxLength(200).IsUnicode(false);
            p.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        });

        modelBuilder.Entity<ProviderFault>(f =>
        {
            f.ToTable("Fault", t => t.HasCheckConstraint("CK_Fault_Mode",
                "Mode IN ('DropResponseAfterCommit', 'DelayResponse', 'StatusUnknown', 'CrashAfterCommit', 'Decline', 'ExpireAuthorization', 'RejectBeforeCommit')"));
            f.HasKey(x => x.Id);
            f.Property(x => x.Label).HasMaxLength(80).IsUnicode(false);
            f.Property(x => x.Match).HasMaxLength(200).IsUnicode(false);
            f.Property(x => x.Mode).HasMaxLength(30).IsUnicode(false);
            f.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            f.HasIndex(x => x.Match);
        });

        modelBuilder.Entity<ProviderStatusQuery>(q =>
        {
            q.ToTable("StatusQuery");
            q.HasKey(x => x.Id);
            q.Property(x => x.OperationKey).HasMaxLength(200).IsUnicode(false);
            q.Property(x => x.Answer).HasMaxLength(16).IsUnicode(false);
            q.Property(x => x.At).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            q.HasIndex(x => x.OperationKey);
        });
    }
}

/// <summary>A committed provider operation; the key is the caller's idempotency key.</summary>
public sealed class ProviderOperation
{
    public string OperationKey { get; private set; } = "";

    public string Kind { get; private set; } = "";

    public byte[] PayloadHash { get; private set; } = [];

    public long AmountMinor { get; private set; }

    public string Currency { get; private set; } = "";

    public string? Reference { get; private set; }

    public string Result { get; private set; } = "";

    public string? ResultCode { get; private set; }

    public DateTime? AuthorizationExpiresAt { get; private set; }

    public DateTime CommittedAt { get; private set; }

    public string? FaultApplied { get; private set; }

    public int Replays { get; private set; }
}

/// <summary>
/// An operation key that can no longer commit: a void or refund named it before it existed. A later request
/// with that key returns the prohibition instead of committing.
/// </summary>
public sealed class ProviderProhibition
{
    public string OperationKey { get; private set; } = "";

    public string ByOperationKey { get; private set; } = "";

    public DateTime CreatedAt { get; private set; }
}

/// <summary>A persisted deterministic fault (drill environment only): an exact operation key, or <c>kind:Authorize</c> etc.</summary>
public sealed class ProviderFault
{
    public long Id { get; private set; }

    public string Label { get; private set; } = "";

    public string Match { get; private set; } = "";

    public string Mode { get; private set; } = "";

    public int? Param { get; private set; }

    /// <summary>Applications left; null means until removed.</summary>
    public int? RemainingUses { get; private set; }

    public DateTime CreatedAt { get; private set; }
}

/// <summary>One answered status query (evidence for reconciliation drills).</summary>
public sealed class ProviderStatusQuery
{
    public long Id { get; private set; }

    public string OperationKey { get; private set; } = "";

    public string Answer { get; private set; } = "";

    public DateTime At { get; private set; }
}

/// <summary>Design-time factory for <c>dotnet ef migrations add</c> (no connection is opened).</summary>
public sealed class ProviderDbDesignTimeFactory : IDesignTimeDbContextFactory<ProviderDbContext>
{
    public ProviderDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ProviderDbContext>();
        ProviderDbContext.Configure(builder, "Server=design-time;Database=PaymentProviderSimDb;Integrated Security=false");
        return new ProviderDbContext(builder.Options);
    }
}
