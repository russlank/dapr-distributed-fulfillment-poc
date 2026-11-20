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

    public DbSet<WorkflowStartAttempt> WorkflowStartAttempts => Set<WorkflowStartAttempt>();

    public DbSet<ExecutionClaim> ExecutionClaims => Set<ExecutionClaim>();

    public DbSet<StubEffect> StubEffects => Set<StubEffect>();

    public DbSet<OrderCheck> OrderChecks => Set<OrderCheck>();

    public DbSet<ReviewTask> ReviewTasks => Set<ReviewTask>();

    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();

    public DbSet<PendingSignal> PendingSignals => Set<PendingSignal>();

    public DbSet<InterventionCase> InterventionCases => Set<InterventionCase>();

    public DbSet<CarrierCallbackInbox> CarrierCallbackInbox => Set<CarrierCallbackInbox>();

    public DbSet<CarrierStatusForward> CarrierStatusForwards => Set<CarrierStatusForward>();

    public DbSet<CarrierCallbackConflict> CarrierCallbackConflicts => Set<CarrierCallbackConflict>();

    public DbSet<OrderOutboxEntry> OrderOutbox => Set<OrderOutboxEntry>();

    public DbSet<Fulfillment.OrderApi.Interventions.RepairStartRequest> RepairStartRequests => Set<Fulfillment.OrderApi.Interventions.RepairStartRequest>();

    public DbSet<SignalAttempt> SignalAttempts => Set<SignalAttempt>();

    public DbSet<ReviewWake> ReviewWakes => Set<ReviewWake>();

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
        ConfigureStartAttempt(modelBuilder.Entity<WorkflowStartAttempt>());
        ConfigureExecutionClaim(modelBuilder.Entity<ExecutionClaim>());
        ConfigureStubEffect(modelBuilder.Entity<StubEffect>());
        ConfigureOrderCheck(modelBuilder.Entity<OrderCheck>());
        ConfigureReviewTask(modelBuilder.Entity<ReviewTask>());
        ConfigureReviewDecision(modelBuilder.Entity<ReviewDecision>());
        ConfigurePendingSignal(modelBuilder.Entity<PendingSignal>());
        ConfigureSignalAttempt(modelBuilder.Entity<SignalAttempt>());
        ConfigureCarrier(modelBuilder);
        ConfigureRepair(modelBuilder);
        ConfigureInterventionCase(modelBuilder.Entity<InterventionCase>());
        ConfigureReviewWake(modelBuilder.Entity<ReviewWake>());
    }

    private static string InList<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))})";

    private static void ConfigureOrder(EntityTypeBuilder<Order> order)
    {
        order.ToTable("Order", t =>
        {
            t.HasCheckConstraint("CK_Order_Status", InList<OrderStatus>("Status"));
            // I15: an order is cancelled or dispatched, never both.
            t.HasCheckConstraint("CK_Order_CancelOrDispatch", "[CancelRequestedAt] IS NULL OR [DispatchStartedAt] IS NULL");
        });
        order.HasKey(o => o.OrderId);
        order.Property(o => o.OrderId).ValueGeneratedNever();
        order.Property(o => o.CustomerId).HasMaxLength(OrderLimits.MaxCustomerIdLength).IsUnicode(false);
        order.Property(o => o.Status).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
        order.Property(o => o.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        order.Property(o => o.CancelRequestedAt).HasColumnType("datetime2(3)");
        order.Property(o => o.DispatchStartedAt).HasColumnType("datetime2(3)");
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
                "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL)"
                + " OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");
        });
        request.HasKey(r => r.OrderId);
        request.Property(r => r.OrderId).ValueGeneratedNever();
        request.Property(r => r.InstanceId).HasMaxLength(64).IsUnicode(false);
        request.Property(r => r.State).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
        request.Property(r => r.NextAttemptAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        request.Property(r => r.LeaseOwner).HasMaxLength(64).IsUnicode(false);
        request.Property(r => r.LeaseExpiresAt).HasColumnType("datetime2(3)");
        request.Property(r => r.LeaseToken);
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

    private static void ConfigureStartAttempt(EntityTypeBuilder<WorkflowStartAttempt> attempt)
    {
        attempt.ToTable("WorkflowStartAttempt", t =>
        {
            t.HasCheckConstraint("CK_WorkflowStartAttempt_Result",
                "[Result] IS NULL OR [Result] IN ('Started', 'AlreadyExists', 'Ambiguous', 'Rejected', 'LeaseLost', 'AlreadyClosed', 'Unexpected')");
        });
        attempt.HasKey(a => a.Id);
        attempt.Property(a => a.Id).UseIdentityColumn();
        attempt.Property(a => a.DispatcherId).HasMaxLength(64).IsUnicode(false);
        attempt.Property(a => a.InstanceId).HasMaxLength(64).IsUnicode(false);
        attempt.Property(a => a.CalledAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        attempt.Property(a => a.CompletedAt).HasColumnType("datetime2(3)");
        attempt.Property(a => a.Result).HasMaxLength(16).IsUnicode(false);
        attempt.Property(a => a.ReportedStatus).HasMaxLength(32).IsUnicode(false);
        attempt.HasIndex(a => a.LeaseToken).IsUnique().HasDatabaseName("UX_WorkflowStartAttempt_LeaseToken");
        attempt.HasIndex(a => new { a.OrderId, a.CalledAt }).HasDatabaseName("IX_WorkflowStartAttempt_Order");
        attempt.HasOne<Order>().WithMany().HasForeignKey(a => a.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureExecutionClaim(EntityTypeBuilder<ExecutionClaim> claim)
    {
        claim.ToTable("ExecutionClaim", t =>
        {
            t.HasCheckConstraint("CK_ExecutionClaim_Outcome", "[Outcome] IN ('Proceed', 'AlreadyTerminal')");
        });
        claim.HasKey(c => c.Id);
        claim.Property(c => c.Id).UseIdentityColumn();
        claim.Property(c => c.InstanceId).HasMaxLength(64).IsUnicode(false);
        claim.Property(c => c.Outcome).HasMaxLength(16).IsUnicode(false);
        claim.Property(c => c.OrderStatus).HasMaxLength(32).IsUnicode(false);
        claim.Property(c => c.ClaimedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        claim.HasIndex(c => new { c.OrderId, c.ClaimedAt }).HasDatabaseName("IX_ExecutionClaim_Order");
        claim.HasOne<Order>().WithMany().HasForeignKey(c => c.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureStubEffect(EntityTypeBuilder<StubEffect> effect)
    {
        effect.ToTable("StubEffect", t =>
        {
            t.HasCheckConstraint("CK_StubEffect_Step", "[Step] LIKE 'stub-%'");
            t.HasCheckConstraint("CK_StubEffect_Replays", "[Replays] >= 0");
        });
        effect.HasKey(e => e.OperationKey);
        effect.Property(e => e.OperationKey).HasMaxLength(200).IsUnicode(false);
        effect.Property(e => e.Step).HasMaxLength(32).IsUnicode(false);
        effect.Property(e => e.Qualifier).HasMaxLength(32).IsUnicode(false);
        effect.Property(e => e.AppliedBy).HasMaxLength(64).IsUnicode(false);
        effect.Property(e => e.AppliedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        effect.HasIndex(e => e.OrderId).HasDatabaseName("IX_StubEffect_Order");
        effect.HasOne<Order>().WithMany().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureOrderCheck(EntityTypeBuilder<OrderCheck> check)
    {
        check.ToTable("OrderCheck", t =>
        {
            t.HasCheckConstraint("CK_OrderCheck_Name", "[CheckName] IN ('price', 'risk')");
            t.HasCheckConstraint("CK_OrderCheck_Replays", "[Replays] >= 0");
        });
        check.HasKey(c => new { c.OrderId, c.CheckName });
        check.Property(c => c.CheckName).HasMaxLength(16).IsUnicode(false);
        check.Property(c => c.OperationKey).HasMaxLength(200).IsUnicode(false);
        check.Property(c => c.Replica).HasMaxLength(64).IsUnicode(false);
        check.Property(c => c.StartedAt).HasColumnType("datetime2(3)");
        check.Property(c => c.CompletedAt).HasColumnType("datetime2(3)");
        check.Property(c => c.Currency).HasMaxLength(3).IsUnicode(false).IsFixedLength();
        check.Property(c => c.RuleVersion).HasMaxLength(32).IsUnicode(false);
        check.Property(c => c.RecordedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        check.HasIndex(c => c.OperationKey).IsUnique().HasDatabaseName("UX_OrderCheck_OperationKey");
        check.HasOne<Order>().WithMany().HasForeignKey(c => c.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureReviewTask(EntityTypeBuilder<ReviewTask> review)
    {
        review.ToTable("ReviewTask", t =>
        {
            t.HasCheckConstraint("CK_ReviewTask_Purpose", "[Purpose] IN ('Approval')");
            t.HasCheckConstraint("CK_ReviewTask_Round", "[Round] >= 1");
            t.HasCheckConstraint("CK_ReviewTask_Status", "[Status] IN ('Pending', 'Approved', 'Rejected', 'Expired', 'Cancelled', 'Escalated')");
            t.HasCheckConstraint("CK_ReviewTask_Lifecycle",
                "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecisionId] IS NULL AND [ExpiredAt] IS NULL)"
                + " OR ([Status] IN ('Approved', 'Rejected') AND [DecidedAt] IS NOT NULL AND [DecisionId] IS NOT NULL AND [ExpiredAt] IS NULL)"
                + " OR ([Status] IN ('Expired', 'Escalated') AND [ExpiredAt] IS NOT NULL AND [DecidedAt] IS NULL AND [DecisionId] IS NULL)"
                + " OR ([Status] = 'Cancelled' AND [DecidedAt] IS NULL AND [DecisionId] IS NULL AND [ExpiredAt] IS NULL)");
        });
        review.HasKey(r => r.ReviewId);
        review.Property(r => r.ReviewId).HasDefaultValueSql("NEWID()");
        review.Property(r => r.Purpose).HasMaxLength(16).IsUnicode(false);
        review.Property(r => r.Status).HasMaxLength(16).IsUnicode(false);
        review.Property(r => r.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        review.Property(r => r.DeadlineAt).HasColumnType("datetime2(3)");
        review.Property(r => r.DecidedAt).HasColumnType("datetime2(3)");
        review.Property(r => r.ExpiredAt).HasColumnType("datetime2(3)");
        review.HasIndex(r => new { r.OrderId, r.Purpose, r.Round }).IsUnique().HasDatabaseName("UX_ReviewTask_Order_Purpose_Round");
        review.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureReviewDecision(EntityTypeBuilder<ReviewDecision> decision)
    {
        decision.ToTable("ReviewDecision", t =>
        {
            t.HasCheckConstraint("CK_ReviewDecision_Outcome", "[Outcome] IN ('Approved', 'Rejected')");
            t.HasCheckConstraint("CK_ReviewDecision_Hash", "DATALENGTH([PayloadHash]) = 32");
        });
        decision.HasKey(d => d.DecisionId);
        decision.Property(d => d.DecisionId).ValueGeneratedNever();
        decision.Property(d => d.Outcome).HasMaxLength(16).IsUnicode(false);
        decision.Property(d => d.OperatorId).HasMaxLength(64).IsUnicode(false);
        decision.Property(d => d.Comment).HasMaxLength(500);
        decision.Property(d => d.PayloadHash).HasColumnType("binary(32)");
        decision.Property(d => d.DecidedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        // At most one accepted decision per review (one outcome per review).
        decision.HasIndex(d => d.ReviewId).IsUnique().HasDatabaseName("UX_ReviewDecision_Review");
        // Cascades from the review only (a second cascade path from the order is not allowed).
        decision.HasOne<ReviewTask>().WithMany().HasForeignKey(d => d.ReviewId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigurePendingSignal(EntityTypeBuilder<PendingSignal> signal)
    {
        signal.ToTable("PendingSignal", t =>
        {
            t.HasCheckConstraint("CK_PendingSignal_State", "[State] IN ('Pending', 'Forwarded', 'Discarded', 'Stuck')");
            t.HasCheckConstraint("CK_PendingSignal_Counters", "[Attempts] >= 0");
            t.HasCheckConstraint("CK_PendingSignal_Lifecycle",
                "([State] = 'Pending' AND [ForwardedAt] IS NULL AND [ClosedAt] IS NULL)"
                + " OR ([State] = 'Forwarded' AND [ForwardedAt] IS NOT NULL AND [LeaseToken] IS NULL)"
                + " OR ([State] IN ('Discarded', 'Stuck') AND [ClosedAt] IS NOT NULL AND [LeaseToken] IS NULL)");
            t.HasCheckConstraint("CK_PendingSignal_Lease",
                "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL)"
                + " OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");
        });
        signal.HasKey(s => s.SignalId);
        signal.Property(s => s.SignalId).HasDefaultValueSql("NEWID()");
        signal.Property(s => s.InstanceId).HasMaxLength(64).IsUnicode(false);
        signal.Property(s => s.EventName).HasMaxLength(100).IsUnicode(false);
        signal.Property(s => s.State).HasMaxLength(16).IsUnicode(false);
        signal.Property(s => s.NextAttemptAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        signal.Property(s => s.LeaseOwner).HasMaxLength(64).IsUnicode(false);
        signal.Property(s => s.LeaseExpiresAt).HasColumnType("datetime2(3)");
        signal.Property(s => s.LastErrorCode).HasMaxLength(64).IsUnicode(false);
        signal.Property(s => s.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        signal.Property(s => s.ForwardedAt).HasColumnType("datetime2(3)");
        signal.Property(s => s.ClosedAt).HasColumnType("datetime2(3)");
        // One signal per causing fact and event.
        signal.HasIndex(s => new { s.CausedBy, s.EventName }).IsUnique().HasDatabaseName("UX_PendingSignal_Cause");
        signal.HasIndex(s => s.NextAttemptAt).HasDatabaseName("IX_PendingSignal_Due")
            .HasFilter("[State] = 'Pending'").IncludeProperties(s => new { s.LeaseExpiresAt });
        signal.HasOne<Order>().WithMany().HasForeignKey(s => s.OrderId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureSignalAttempt(EntityTypeBuilder<SignalAttempt> attempt)
    {
        attempt.ToTable("SignalAttempt", t =>
        {
            t.HasCheckConstraint("CK_SignalAttempt_Result",
                "[Result] IS NULL OR [Result] IN ('Forwarded', 'Discarded', 'Stuck', 'NotStarted', 'NotRunning', 'Ambiguous', 'Rejected', 'LeaseLost', 'Unexpected')");
        });
        attempt.HasKey(a => a.Id);
        attempt.Property(a => a.Id).UseIdentityColumn();
        attempt.Property(a => a.DispatcherId).HasMaxLength(64).IsUnicode(false);
        attempt.Property(a => a.CalledAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        attempt.Property(a => a.CompletedAt).HasColumnType("datetime2(3)");
        attempt.Property(a => a.Result).HasMaxLength(16).IsUnicode(false);
        attempt.Property(a => a.ReportedStatus).HasMaxLength(32).IsUnicode(false);
        attempt.Property(a => a.TransportError).HasMaxLength(200).IsUnicode(false);
        attempt.HasIndex(a => a.LeaseToken).IsUnique().HasDatabaseName("UX_SignalAttempt_LeaseToken");
        attempt.HasIndex(a => new { a.SignalId, a.CalledAt }).HasDatabaseName("IX_SignalAttempt_Signal");
        attempt.HasOne<PendingSignal>().WithMany().HasForeignKey(a => a.SignalId).OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureRepair(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Fulfillment.OrderApi.Interventions.RepairStartRequest>(r =>
        {
            r.ToTable("RepairStartRequest", t =>
            {
                t.HasCheckConstraint("CK_RepairStartRequest_State", "[State] IN ('Pending', 'Started', 'Closed')");
                t.HasCheckConstraint("CK_RepairStartRequest_Lease",
                    "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");
            });
            r.HasKey(x => x.CaseId);
            r.Property(x => x.InstanceId).HasMaxLength(80).IsUnicode(false);
            r.Property(x => x.State).HasMaxLength(16).IsUnicode(false);
            r.Property(x => x.NextAttemptAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            r.Property(x => x.LeaseOwner).HasMaxLength(64).IsUnicode(false);
            r.Property(x => x.LeaseExpiresAt).HasColumnType("datetime2(3)");
            r.Property(x => x.LastError).HasMaxLength(200);
            r.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            r.Property(x => x.StartedAt).HasColumnType("datetime2(3)");
            r.Property(x => x.ClosedAt).HasColumnType("datetime2(3)");
            r.HasOne<InterventionCase>().WithOne().HasForeignKey<Fulfillment.OrderApi.Interventions.RepairStartRequest>(x => x.CaseId);
            r.HasIndex(x => x.InstanceId).IsUnique();
        });

    private static void ConfigureCarrier(ModelBuilder modelBuilder)
    {
        const string statuses = "('Accepted', 'PickedUp', 'InTransit', 'Delivered', 'Exception')";
        modelBuilder.Entity<CarrierCallbackInbox>(inbox =>
        {
            inbox.ToTable("CarrierCallbackInbox", t => t.HasCheckConstraint("CK_CarrierCallbackInbox_Status", $"[Status] IN {statuses}"));
            inbox.HasKey(x => x.Id);
            inbox.Property(x => x.Source).HasMaxLength(16).IsUnicode(false);
            inbox.Property(x => x.PayloadHash).HasMaxLength(64).IsUnicode(false);
            inbox.Property(x => x.Status).HasMaxLength(16).IsUnicode(false);
            inbox.Property(x => x.OccurredAt).HasColumnType("datetime2(3)");
            inbox.Property(x => x.ReceivedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            inbox.HasIndex(x => new { x.Source, x.EventId }).IsUnique().HasDatabaseName("UX_CarrierCallbackInbox_Event");
            inbox.HasIndex(x => x.ShipmentId);
        });
        modelBuilder.Entity<CarrierStatusForward>(f =>
        {
            f.ToTable("CarrierStatusForward", t =>
            {
                t.HasCheckConstraint("CK_CarrierStatusForward_State", "[State] IN ('Pending', 'Processed')");
                t.HasCheckConstraint("CK_CarrierStatusForward_Processed", "([State] = 'Processed' AND [Outcome] IS NOT NULL AND [ProcessedAt] IS NOT NULL AND [LeaseToken] IS NULL) OR ([State] = 'Pending' AND [Outcome] IS NULL AND [ProcessedAt] IS NULL)");
                t.HasCheckConstraint("CK_CarrierStatusForward_Lease",
                    "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");
            });
            f.HasKey(x => x.EventId);
            f.Property(x => x.State).HasMaxLength(16).IsUnicode(false);
            f.Property(x => x.Outcome).HasMaxLength(24).IsUnicode(false);
            f.Property(x => x.NextAttemptAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            f.Property(x => x.LeaseOwner).HasMaxLength(64).IsUnicode(false);
            f.Property(x => x.LeaseExpiresAt).HasColumnType("datetime2(3)");
            f.Property(x => x.LastError).HasMaxLength(200);
            f.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            f.Property(x => x.ProcessedAt).HasColumnType("datetime2(3)");
            f.HasIndex(x => new { x.State, x.NextAttemptAt });
        });
        modelBuilder.Entity<CarrierCallbackConflict>(c =>
        {
            c.ToTable("CarrierCallbackConflict");
            c.HasKey(x => x.Id);
            c.Property(x => x.StoredHash).HasMaxLength(64).IsUnicode(false);
            c.Property(x => x.ReceivedHash).HasMaxLength(64).IsUnicode(false);
            c.Property(x => x.DetectedBy).HasMaxLength(16).IsUnicode(false);
            c.Property(x => x.DetectedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        });
        modelBuilder.Entity<OrderOutboxEntry>(o =>
        {
            o.ToTable("OrderOutbox", t =>
            {
                t.HasCheckConstraint("CK_OrderOutbox_EventType", "[EventType] IN ('OrderCompleted')");
                t.HasCheckConstraint("CK_OrderOutbox_State", "[State] IN ('Pending', 'Published')");
            });
            o.HasKey(x => x.Id);
            o.Property(x => x.EventType).HasMaxLength(32).IsUnicode(false);
            o.Property(x => x.State).HasMaxLength(16).IsUnicode(false);
            o.Property(x => x.CreatedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            o.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
            // One OrderCompleted per order.
            o.HasIndex(x => new { x.OrderId, x.EventType }).IsUnique().HasDatabaseName("UX_OrderOutbox_Event");
        });
    }

    private static void ConfigureInterventionCase(EntityTypeBuilder<InterventionCase> c)
    {
        c.ToTable("InterventionCase", t =>
        {
            t.HasCheckConstraint("CK_InterventionCase_State", "[State] IN ('Open', 'Reconciling', 'Resolved', 'Abandoned')");
            t.HasCheckConstraint("CK_InterventionCase_Kind",
                "[Kind] IN ('AmbiguousPayment', 'AmbiguousDispatch', 'CarrierTerminalConflict', 'DeliveryException', 'DeliveryOverdue', 'CompensationExhausted', 'WorkflowFaulted', 'WorkflowMissing', 'ExecutionMismatch', 'SignalStuck', 'ProjectionGap')");
            t.HasCheckConstraint("CK_InterventionCase_Resolution",
                "([State] = 'Resolved' AND [ResolutionAction] IS NOT NULL AND [ResolutionPath] IS NOT NULL AND [ResolvedAt] IS NOT NULL)"
                + " OR ([State] <> 'Resolved' AND [ResolutionAction] IS NULL AND [ResolutionPath] IS NULL AND [ResolvedAt] IS NULL)");
            t.HasCheckConstraint("CK_InterventionCase_Action", "[ResolutionAction] IS NULL OR [ResolutionAction] IN ('ConfirmExternalSuccess', 'ConfirmExternalFailure', 'Acknowledge')");
            t.HasCheckConstraint("CK_InterventionCase_Path", "[ResolutionPath] IS NULL OR [ResolutionPath] IN ('Signal', 'Repair', 'RecordOnly')");
        });
        c.HasKey(x => x.CaseId);
        c.Property(x => x.CaseId).HasDefaultValueSql("NEWSEQUENTIALID()");
        c.Property(x => x.Kind).HasMaxLength(32).IsUnicode(false);
        c.Property(x => x.State).HasMaxLength(16).IsUnicode(false);
        c.Property(x => x.OperationKey).HasMaxLength(200).IsUnicode(false);
        c.Property(x => x.Evidence).HasMaxLength(2000);
        c.Property(x => x.ResumeStatus).HasMaxLength(32).IsUnicode(false);
        c.Property(x => x.OpenedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        c.Property(x => x.ResolutionAction).HasMaxLength(32).IsUnicode(false);
        c.Property(x => x.ResolutionPath).HasMaxLength(16).IsUnicode(false);
        c.Property(x => x.ResolutionEvidence).HasMaxLength(2000);
        c.Property(x => x.ResolvedBy).HasMaxLength(64).IsUnicode(false);
        c.Property(x => x.ResolvedAt).HasColumnType("datetime2(3)");
        c.Property(x => x.RepairOutcome).HasMaxLength(64).IsUnicode(false);
        c.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
        c.HasIndex(x => x.OperationKey).IsUnique().HasDatabaseName("UX_InterventionCase_OperationKey");
        // One open case per order and kind.
        c.HasIndex(x => new { x.OrderId, x.Kind }).IsUnique().HasFilter("[State] IN ('Open', 'Reconciling')").HasDatabaseName("UX_InterventionCase_OpenPerKind");
    }

    private static void ConfigureReviewWake(EntityTypeBuilder<ReviewWake> wake)
    {
        wake.ToTable("ReviewWake", t =>
        {
            t.HasCheckConstraint("CK_ReviewWake_Wake", "[Wake] IN ('Initial', 'Signal', 'Timer', 'Recheck')");
        });
        wake.HasKey(w => w.Id);
        wake.Property(w => w.Id).UseIdentityColumn();
        wake.Property(w => w.Wake).HasMaxLength(16).IsUnicode(false);
        wake.Property(w => w.SeenStatus).HasMaxLength(16).IsUnicode(false);
        wake.Property(w => w.At).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
        wake.HasIndex(w => new { w.ReviewId, w.At }).HasDatabaseName("IX_ReviewWake_Review");
        wake.HasOne<ReviewTask>().WithMany().HasForeignKey(w => w.ReviewId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Constraint names the code matches on (unique-violation handling).</summary>
public static class OrderDbConstraints
{
    public const string IdempotencyKeyPrimaryKey = "PK_IdempotencyKey";
}
