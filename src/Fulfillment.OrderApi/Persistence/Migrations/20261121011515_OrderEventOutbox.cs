using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderEventOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_OrderOutbox_Event",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderOutbox_EventType",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.AlterColumn<string>(
                name: "State",
                schema: "orders",
                table: "OrderOutbox",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                defaultValue: "Pending",
                oldClrType: typeof(string),
                oldType: "varchar(16)",
                oldUnicode: false,
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<string>(
                name: "Payload",
                schema: "orders",
                table: "OrderOutbox",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                schema: "orders",
                table: "OrderOutbox",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                schema: "orders",
                table: "OrderOutbox",
                type: "uniqueidentifier",
                nullable: false,
                defaultValueSql: "NEWID()");

            migrationBuilder.AddColumn<int>(
                name: "Generation",
                schema: "orders",
                table: "OrderOutbox",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                schema: "orders",
                table: "OrderOutbox",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                schema: "orders",
                table: "OrderOutbox",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                schema: "orders",
                table: "OrderOutbox",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                schema: "orders",
                table: "OrderOutbox",
                type: "datetime2(3)",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                schema: "orders",
                table: "OrderOutbox",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchemaVersion",
                schema: "orders",
                table: "OrderOutbox",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                schema: "orders",
                table: "OrderOutbox",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Stalled",
                schema: "orders",
                table: "OrderOutbox",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Existing rows: the OrderCompleted row takes its history row's sequence, and every other history row
            // gets its event, so every order's stream is complete and gapless from 1.
            migrationBuilder.Sql("""
                UPDATE o SET Sequence = h.Sequence
                FROM orders.OrderOutbox o
                CROSS APPLY (SELECT TOP (1) x.Sequence FROM orders.OrderStatusHistory x
                             WHERE x.OrderId = o.OrderId AND x.ToStatus = 'Completed' AND (x.FromStatus IS NULL OR x.FromStatus <> 'Completed')
                             ORDER BY x.Sequence) h;
                INSERT orders.OrderOutbox (EventId, OrderId, Sequence, EventType, Payload, State)
                SELECT NEWID(), h.OrderId, h.Sequence, 'OrderStatusChanged',
                       CONCAT('{"fromStatus":', CASE WHEN h.FromStatus IS NULL THEN 'null' ELSE CONCAT('"', h.FromStatus, '"') END,
                              ',"toStatus":"', h.ToStatus, '","reasonCode":"', h.ReasonCode, '"}'), 'Pending'
                FROM orders.OrderStatusHistory h
                WHERE NOT EXISTS (SELECT 1 FROM orders.OrderOutbox o WHERE o.OrderId = h.OrderId AND o.Sequence = h.Sequence);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OrderOutbox_Due",
                schema: "orders",
                table: "OrderOutbox",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_OrderOutbox_Completed",
                schema: "orders",
                table: "OrderOutbox",
                column: "OrderId",
                unique: true,
                filter: "[EventType] = 'OrderCompleted'");

            migrationBuilder.CreateIndex(
                name: "UX_OrderOutbox_EventId",
                schema: "orders",
                table: "OrderOutbox",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_OrderOutbox_Order_Sequence",
                schema: "orders",
                table: "OrderOutbox",
                columns: new[] { "OrderId", "Sequence" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderOutbox_EventType",
                schema: "orders",
                table: "OrderOutbox",
                sql: "[EventType] IN ('OrderStatusChanged', 'OrderCompleted')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderOutbox_Sequence",
                schema: "orders",
                table: "OrderOutbox",
                sql: "[Sequence] >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderOutbox_Due",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropIndex(
                name: "UX_OrderOutbox_Completed",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropIndex(
                name: "UX_OrderOutbox_EventId",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropIndex(
                name: "UX_OrderOutbox_Order_Sequence",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderOutbox_EventType",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderOutbox_Sequence",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "Attempts",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "EventId",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "Generation",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "LastError",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "Sequence",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.DropColumn(
                name: "Stalled",
                schema: "orders",
                table: "OrderOutbox");

            migrationBuilder.AlterColumn<string>(
                name: "State",
                schema: "orders",
                table: "OrderOutbox",
                type: "varchar(16)",
                unicode: false,
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(16)",
                oldUnicode: false,
                oldMaxLength: 16,
                oldDefaultValue: "Pending");

            migrationBuilder.AlterColumn<string>(
                name: "Payload",
                schema: "orders",
                table: "OrderOutbox",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(2000)",
                oldMaxLength: 2000);

            migrationBuilder.CreateIndex(
                name: "UX_OrderOutbox_Event",
                schema: "orders",
                table: "OrderOutbox",
                columns: new[] { "OrderId", "EventType" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderOutbox_EventType",
                schema: "orders",
                table: "OrderOutbox",
                sql: "[EventType] IN ('OrderCompleted')");
        }
    }
}
