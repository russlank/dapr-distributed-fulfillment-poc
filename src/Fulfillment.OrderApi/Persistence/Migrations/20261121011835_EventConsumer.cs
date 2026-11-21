using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EventConsumer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventDeadLetter",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MessageId = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: true),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: true),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    Reason = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Topic = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventDeadLetter", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderEventHistory",
                schema: "orders",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    PayloadHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Disposition = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Duplicates = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderEventHistory", x => x.EventId);
                    table.CheckConstraint("CK_OrderEventHistory_Applied", "([Disposition] = 'Applied' AND [AppliedAt] IS NOT NULL) OR ([Disposition] = 'Pending' AND [AppliedAt] IS NULL)");
                    table.CheckConstraint("CK_OrderEventHistory_Disposition", "[Disposition] IN ('Applied', 'Pending')");
                    table.CheckConstraint("CK_OrderEventHistory_Sequence", "[Sequence] >= 1");
                    table.CheckConstraint("CK_OrderEventHistory_Source", "[Source] IN ('orders', 'inventory', 'payments', 'shipping')");
                });

            migrationBuilder.CreateTable(
                name: "OrderTimelineState",
                schema: "orders",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    LastAppliedSequence = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Fields = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "{}"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderTimelineState", x => new { x.OrderId, x.Source });
                    table.CheckConstraint("CK_OrderTimelineState_Sequence", "[LastAppliedSequence] >= 0");
                    table.CheckConstraint("CK_OrderTimelineState_Source", "[Source] IN ('orders', 'inventory', 'payments', 'shipping')");
                });

            migrationBuilder.CreateTable(
                name: "ReconcilerLease",
                schema: "orders",
                columns: table => new
                {
                    Name = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Owner = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconcilerLease", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventDeadLetter_EventId",
                schema: "orders",
                table: "EventDeadLetter",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderEventHistory_Pending",
                schema: "orders",
                table: "OrderEventHistory",
                columns: new[] { "Disposition", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_OrderEventHistory_Stream",
                schema: "orders",
                table: "OrderEventHistory",
                columns: new[] { "Source", "OrderId", "Sequence" },
                unique: true);

            // Gaps are derived, never stored: for each stream with pending events, every missing sequence between
            // the last applied one and the highest pending one.
            migrationBuilder.Sql("""
                CREATE VIEW orders.OrderEventGaps AS
                SELECT s.OrderId, s.Source, s.LastAppliedSequence, n.MissingSequence, p.OldestPendingAt, p.PendingEvents
                FROM orders.OrderTimelineState s
                CROSS APPLY (SELECT MAX(h.Sequence) AS MaxPending, MIN(h.ReceivedAt) AS OldestPendingAt, COUNT(*) AS PendingEvents
                             FROM orders.OrderEventHistory h
                             WHERE h.OrderId = s.OrderId AND h.Source = s.Source AND h.Disposition = 'Pending') p
                CROSS APPLY (SELECT TOP (ISNULL(p.MaxPending, 0) - s.LastAppliedSequence)
                                    s.LastAppliedSequence + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS MissingSequence
                             FROM sys.all_columns) n
                WHERE p.MaxPending > s.LastAppliedSequence
                  AND NOT EXISTS (SELECT 1 FROM orders.OrderEventHistory h
                                  WHERE h.OrderId = s.OrderId AND h.Source = s.Source AND h.Sequence = n.MissingSequence);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW orders.OrderEventGaps;");

            migrationBuilder.DropTable(
                name: "EventDeadLetter",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OrderEventHistory",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OrderTimelineState",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "ReconcilerLease",
                schema: "orders");
        }
    }
}
