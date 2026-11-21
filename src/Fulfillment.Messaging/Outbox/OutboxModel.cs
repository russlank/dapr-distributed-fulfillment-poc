using Microsoft.EntityFrameworkCore;

namespace Fulfillment.Messaging.Outbox;

/// <summary>An outbox row of a source that has no outbox of its own shape (payments, shipping). The model exists for migrations.</summary>
public sealed class OutboxEvent
{
    public long Id { get; private set; }

    public Guid EventId { get; private set; }

    public Guid OrderId { get; private set; }

    public int Sequence { get; private set; }

    public string EventType { get; private set; } = "";

    public int SchemaVersion { get; private set; }

    public string Payload { get; private set; } = "";

    public string State { get; private set; } = "";

    public int Attempts { get; private set; }

    public DateTime NextAttemptAt { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseUntil { get; private set; }

    public int Generation { get; private set; }

    public bool Stalled { get; private set; }

    public string? LastError { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? PublishedAt { get; private set; }
}

public static class OutboxModel
{
    /// <summary>
    /// Table <c>Outbox</c> in the context's default schema: gapless sequence per order (unique), states
    /// <c>Pending</c>/<c>Published</c>, the relay's lease and retry columns, and the source's event types.
    /// </summary>
    public static void Configure(ModelBuilder modelBuilder, IReadOnlyCollection<string> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(eventTypes);
        modelBuilder.Entity<OutboxEvent>(o =>
        {
            o.ToTable("Outbox", t =>
            {
                t.HasCheckConstraint("CK_Outbox_Sequence", "[Sequence] >= 1");
                t.HasCheckConstraint("CK_Outbox_State", "[State] IN ('Pending', 'Published')");
                t.HasCheckConstraint("CK_Outbox_EventType", $"[EventType] IN ({string.Join(", ", eventTypes.Select(e => $"'{e}'"))})");
            });
            o.HasKey(x => x.Id);
            o.Property(x => x.Id).UseIdentityColumn();
            o.Property(x => x.EventId).HasDefaultValueSql("NEWID()");
            o.HasIndex(x => x.EventId).IsUnique().HasDatabaseName("UX_Outbox_EventId");
            o.HasIndex(x => new { x.OrderId, x.Sequence }).IsUnique().HasDatabaseName("UX_Outbox_Order_Sequence");
            o.HasIndex(x => new { x.State, x.NextAttemptAt }).HasDatabaseName("IX_Outbox_Due");
            o.Property(x => x.EventType).HasMaxLength(40).IsUnicode(false);
            o.Property(x => x.SchemaVersion).HasDefaultValue(1);
            o.Property(x => x.Payload).HasMaxLength(2000);
            o.Property(x => x.State).HasMaxLength(16).IsUnicode(false).HasDefaultValue("Pending");
            o.Property(x => x.Attempts).HasDefaultValue(0);
            o.Property(x => x.NextAttemptAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            o.Property(x => x.LeaseOwner).HasMaxLength(64).IsUnicode(false);
            o.Property(x => x.LeaseUntil).HasColumnType("datetime2(3)");
            o.Property(x => x.Generation).HasDefaultValue(0);
            o.Property(x => x.Stalled).HasDefaultValue(false);
            o.Property(x => x.LastError).HasMaxLength(200);
            o.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            o.Property(x => x.PublishedAt).HasColumnType("datetime2(3)");
        });
    }
}
