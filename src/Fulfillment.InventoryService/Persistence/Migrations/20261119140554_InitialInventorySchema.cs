using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Fulfillment.InventoryService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "OrderClosure",
                schema: "inventory",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderClosure", x => x.OrderId);
                    table.CheckConstraint("CK_OrderClosure_Reason", "[Reason] IN ('Released', 'Consumed')");
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    HoldId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, defaultValue: "Pending")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                    table.CheckConstraint("CK_Outbox_EventType", "[EventType] IN ('HoldCreated', 'HoldReleased', 'HoldExpired', 'HoldConsumed')");
                    table.CheckConstraint("CK_Outbox_Sequence", "[Sequence] >= 1");
                    table.CheckConstraint("CK_Outbox_State", "[State] IN ('Pending', 'Published')");
                });

            migrationBuilder.CreateTable(
                name: "ProcessedOperation",
                schema: "inventory",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Operation = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    HoldId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WarehouseId = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ProcessedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Replays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedOperation", x => x.OperationKey);
                    table.CheckConstraint("CK_ProcessedOperation_Operation", "[Operation] IN ('Reserve', 'Release', 'Consume')");
                    table.CheckConstraint("CK_ProcessedOperation_Outcome", "[Outcome] IN ('Reserved', 'InsufficientStock', 'OrderClosed', 'Released', 'NotHeld', 'Consumed', 'HoldExpired', 'Incomplete')");
                });

            migrationBuilder.CreateTable(
                name: "SweeperLease",
                schema: "inventory",
                columns: table => new
                {
                    Name = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Owner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Token = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweeperLease", x => x.Name);
                    table.CheckConstraint("CK_SweeperLease_Owner", "([Owner] IS NULL AND [Token] IS NULL AND [ExpiresAt] IS NULL) OR ([Owner] IS NOT NULL AND [Token] IS NOT NULL AND [ExpiresAt] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Warehouse",
                schema: "inventory",
                columns: table => new
                {
                    WarehouseId = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouse", x => x.WarehouseId);
                    table.CheckConstraint("CK_Warehouse_Priority", "[Priority] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "StockItem",
                schema: "inventory",
                columns: table => new
                {
                    WarehouseId = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Sku = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    OnHand = table.Column<int>(type: "int", nullable: false),
                    Reserved = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockItem", x => new { x.WarehouseId, x.Sku });
                    table.CheckConstraint("CK_StockItem_OnHand", "[OnHand] >= 0");
                    table.CheckConstraint("CK_StockItem_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
                    table.ForeignKey(
                        name: "FK_StockItem_Warehouse_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "inventory",
                        principalTable: "Warehouse",
                        principalColumn: "WarehouseId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Hold",
                schema: "inventory",
                columns: table => new
                {
                    HoldId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    WarehouseId = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Sku = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedBy = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hold", x => x.HoldId);
                    table.CheckConstraint("CK_Hold_Expiry", "[ExpiresAt] > [CreatedAt]");
                    table.CheckConstraint("CK_Hold_Lifecycle", "([State] = 'Held' AND [ClosedAt] IS NULL AND [ClosedBy] IS NULL) OR ([State] <> 'Held' AND [ClosedAt] IS NOT NULL AND [ClosedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_Hold_Quantity", "[Quantity] > 0");
                    table.CheckConstraint("CK_Hold_State", "[State] IN ('Held', 'Released', 'Expired', 'Consumed')");
                    table.ForeignKey(
                        name: "FK_Hold_StockItem_WarehouseId_Sku",
                        columns: x => new { x.WarehouseId, x.Sku },
                        principalSchema: "inventory",
                        principalTable: "StockItem",
                        principalColumns: new[] { "WarehouseId", "Sku" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "SweeperLease",
                columns: new[] { "Name", "ExpiresAt", "Owner", "Token" },
                values: new object[] { "hold-expiry", null, null, null });

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "Warehouse",
                columns: new[] { "WarehouseId", "Name", "Priority" },
                values: new object[,]
                {
                    { "W1", "North", 1 },
                    { "W2", "Central", 2 },
                    { "W3", "South", 3 }
                });

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "StockItem",
                columns: new[] { "Sku", "WarehouseId", "OnHand", "Reserved" },
                values: new object[,]
                {
                    { "SKU-1", "W1", 1000000, 0 },
                    { "SKU-10", "W1", 1000000, 0 },
                    { "SKU-100", "W1", 1000000, 0 },
                    { "SKU-11", "W1", 1000000, 0 },
                    { "SKU-12", "W1", 1000000, 0 },
                    { "SKU-13", "W1", 1000000, 0 },
                    { "SKU-14", "W1", 1000000, 0 },
                    { "SKU-15", "W1", 1000000, 0 },
                    { "SKU-16", "W1", 1000000, 0 },
                    { "SKU-17", "W1", 1000000, 0 },
                    { "SKU-18", "W1", 1000000, 0 },
                    { "SKU-19", "W1", 1000000, 0 },
                    { "SKU-2", "W1", 1000000, 0 },
                    { "SKU-20", "W1", 1000000, 0 },
                    { "SKU-21", "W1", 1000000, 0 },
                    { "SKU-22", "W1", 1000000, 0 },
                    { "SKU-23", "W1", 1000000, 0 },
                    { "SKU-24", "W1", 1000000, 0 },
                    { "SKU-25", "W1", 1000000, 0 },
                    { "SKU-26", "W1", 1000000, 0 },
                    { "SKU-27", "W1", 1000000, 0 },
                    { "SKU-28", "W1", 1000000, 0 },
                    { "SKU-29", "W1", 1000000, 0 },
                    { "SKU-3", "W1", 1000000, 0 },
                    { "SKU-30", "W1", 1000000, 0 },
                    { "SKU-31", "W1", 1000000, 0 },
                    { "SKU-32", "W1", 1000000, 0 },
                    { "SKU-33", "W1", 1000000, 0 },
                    { "SKU-34", "W1", 1000000, 0 },
                    { "SKU-35", "W1", 1000000, 0 },
                    { "SKU-36", "W1", 1000000, 0 },
                    { "SKU-37", "W1", 1000000, 0 },
                    { "SKU-38", "W1", 1000000, 0 },
                    { "SKU-39", "W1", 1000000, 0 },
                    { "SKU-4", "W1", 1000000, 0 },
                    { "SKU-40", "W1", 1000000, 0 },
                    { "SKU-41", "W1", 1000000, 0 },
                    { "SKU-42", "W1", 1000000, 0 },
                    { "SKU-43", "W1", 1000000, 0 },
                    { "SKU-44", "W1", 1000000, 0 },
                    { "SKU-45", "W1", 1000000, 0 },
                    { "SKU-46", "W1", 1000000, 0 },
                    { "SKU-47", "W1", 1000000, 0 },
                    { "SKU-48", "W1", 1000000, 0 },
                    { "SKU-49", "W1", 1000000, 0 },
                    { "SKU-5", "W1", 1000000, 0 },
                    { "SKU-50", "W1", 1000000, 0 },
                    { "SKU-51", "W1", 1000000, 0 },
                    { "SKU-52", "W1", 1000000, 0 },
                    { "SKU-53", "W1", 1000000, 0 },
                    { "SKU-54", "W1", 1000000, 0 },
                    { "SKU-55", "W1", 1000000, 0 },
                    { "SKU-56", "W1", 1000000, 0 },
                    { "SKU-57", "W1", 1000000, 0 },
                    { "SKU-58", "W1", 1000000, 0 },
                    { "SKU-59", "W1", 1000000, 0 },
                    { "SKU-6", "W1", 1000000, 0 },
                    { "SKU-60", "W1", 1000000, 0 },
                    { "SKU-61", "W1", 1000000, 0 },
                    { "SKU-62", "W1", 1000000, 0 },
                    { "SKU-63", "W1", 1000000, 0 },
                    { "SKU-64", "W1", 1000000, 0 },
                    { "SKU-65", "W1", 1000000, 0 },
                    { "SKU-66", "W1", 1000000, 0 },
                    { "SKU-67", "W1", 1000000, 0 },
                    { "SKU-68", "W1", 1000000, 0 },
                    { "SKU-69", "W1", 1000000, 0 },
                    { "SKU-7", "W1", 1000000, 0 },
                    { "SKU-70", "W1", 1000000, 0 },
                    { "SKU-71", "W1", 1000000, 0 },
                    { "SKU-72", "W1", 1000000, 0 },
                    { "SKU-73", "W1", 1000000, 0 },
                    { "SKU-74", "W1", 1000000, 0 },
                    { "SKU-75", "W1", 1000000, 0 },
                    { "SKU-76", "W1", 1000000, 0 },
                    { "SKU-77", "W1", 1000000, 0 },
                    { "SKU-78", "W1", 1000000, 0 },
                    { "SKU-79", "W1", 1000000, 0 },
                    { "SKU-8", "W1", 1000000, 0 },
                    { "SKU-80", "W1", 1000000, 0 },
                    { "SKU-81", "W1", 1000000, 0 },
                    { "SKU-82", "W1", 1000000, 0 },
                    { "SKU-83", "W1", 1000000, 0 },
                    { "SKU-84", "W1", 1000000, 0 },
                    { "SKU-85", "W1", 1000000, 0 },
                    { "SKU-86", "W1", 1000000, 0 },
                    { "SKU-87", "W1", 1000000, 0 },
                    { "SKU-88", "W1", 1000000, 0 },
                    { "SKU-89", "W1", 1000000, 0 },
                    { "SKU-9", "W1", 1000000, 0 },
                    { "SKU-90", "W1", 1000000, 0 },
                    { "SKU-91", "W1", 1000000, 0 },
                    { "SKU-92", "W1", 1000000, 0 },
                    { "SKU-93", "W1", 1000000, 0 },
                    { "SKU-94", "W1", 1000000, 0 },
                    { "SKU-95", "W1", 1000000, 0 },
                    { "SKU-96", "W1", 1000000, 0 },
                    { "SKU-97", "W1", 1000000, 0 },
                    { "SKU-98", "W1", 1000000, 0 },
                    { "SKU-99", "W1", 1000000, 0 },
                    { "SKU-R1", "W1", 1000000, 0 },
                    { "SKU-R10", "W1", 1000000, 0 },
                    { "SKU-R11", "W1", 1000000, 0 },
                    { "SKU-R12", "W1", 1000000, 0 },
                    { "SKU-R13", "W1", 1000000, 0 },
                    { "SKU-R14", "W1", 1000000, 0 },
                    { "SKU-R15", "W1", 1000000, 0 },
                    { "SKU-R16", "W1", 1000000, 0 },
                    { "SKU-R17", "W1", 1000000, 0 },
                    { "SKU-R18", "W1", 1000000, 0 },
                    { "SKU-R19", "W1", 1000000, 0 },
                    { "SKU-R2", "W1", 1000000, 0 },
                    { "SKU-R20", "W1", 1000000, 0 },
                    { "SKU-R21", "W1", 1000000, 0 },
                    { "SKU-R22", "W1", 1000000, 0 },
                    { "SKU-R23", "W1", 1000000, 0 },
                    { "SKU-R24", "W1", 1000000, 0 },
                    { "SKU-R25", "W1", 1000000, 0 },
                    { "SKU-R26", "W1", 1000000, 0 },
                    { "SKU-R27", "W1", 1000000, 0 },
                    { "SKU-R28", "W1", 1000000, 0 },
                    { "SKU-R29", "W1", 1000000, 0 },
                    { "SKU-R3", "W1", 1000000, 0 },
                    { "SKU-R30", "W1", 1000000, 0 },
                    { "SKU-R31", "W1", 1000000, 0 },
                    { "SKU-R32", "W1", 1000000, 0 },
                    { "SKU-R33", "W1", 1000000, 0 },
                    { "SKU-R34", "W1", 1000000, 0 },
                    { "SKU-R35", "W1", 1000000, 0 },
                    { "SKU-R36", "W1", 1000000, 0 },
                    { "SKU-R37", "W1", 1000000, 0 },
                    { "SKU-R38", "W1", 1000000, 0 },
                    { "SKU-R39", "W1", 1000000, 0 },
                    { "SKU-R4", "W1", 1000000, 0 },
                    { "SKU-R40", "W1", 1000000, 0 },
                    { "SKU-R41", "W1", 1000000, 0 },
                    { "SKU-R42", "W1", 1000000, 0 },
                    { "SKU-R43", "W1", 1000000, 0 },
                    { "SKU-R44", "W1", 1000000, 0 },
                    { "SKU-R45", "W1", 1000000, 0 },
                    { "SKU-R46", "W1", 1000000, 0 },
                    { "SKU-R47", "W1", 1000000, 0 },
                    { "SKU-R48", "W1", 1000000, 0 },
                    { "SKU-R49", "W1", 1000000, 0 },
                    { "SKU-R5", "W1", 1000000, 0 },
                    { "SKU-R50", "W1", 1000000, 0 },
                    { "SKU-R6", "W1", 1000000, 0 },
                    { "SKU-R7", "W1", 1000000, 0 },
                    { "SKU-R8", "W1", 1000000, 0 },
                    { "SKU-R9", "W1", 1000000, 0 },
                    { "SKU-X1", "W1", 1000000, 0 },
                    { "SKU-X10", "W1", 1000000, 0 },
                    { "SKU-X11", "W1", 1000000, 0 },
                    { "SKU-X12", "W1", 1000000, 0 },
                    { "SKU-X13", "W1", 1000000, 0 },
                    { "SKU-X14", "W1", 1000000, 0 },
                    { "SKU-X15", "W1", 1000000, 0 },
                    { "SKU-X16", "W1", 1000000, 0 },
                    { "SKU-X17", "W1", 1000000, 0 },
                    { "SKU-X18", "W1", 1000000, 0 },
                    { "SKU-X19", "W1", 1000000, 0 },
                    { "SKU-X2", "W1", 1000000, 0 },
                    { "SKU-X20", "W1", 1000000, 0 },
                    { "SKU-X3", "W1", 1000000, 0 },
                    { "SKU-X4", "W1", 1000000, 0 },
                    { "SKU-X5", "W1", 1000000, 0 },
                    { "SKU-X6", "W1", 1000000, 0 },
                    { "SKU-X7", "W1", 1000000, 0 },
                    { "SKU-X8", "W1", 1000000, 0 },
                    { "SKU-X9", "W1", 1000000, 0 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Hold_Due",
                schema: "inventory",
                table: "Hold",
                column: "ExpiresAt",
                filter: "[State] = 'Held'");

            migrationBuilder.CreateIndex(
                name: "IX_Hold_WarehouseId_Sku",
                schema: "inventory",
                table: "Hold",
                columns: new[] { "WarehouseId", "Sku" });

            migrationBuilder.CreateIndex(
                name: "UX_Hold_Order_Line",
                schema: "inventory",
                table: "Hold",
                columns: new[] { "OrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Outbox_Order_Sequence",
                schema: "inventory",
                table: "Outbox",
                columns: new[] { "OrderId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedOperation_Order",
                schema: "inventory",
                table: "ProcessedOperation",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "UX_Warehouse_Priority",
                schema: "inventory",
                table: "Warehouse",
                column: "Priority",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Hold",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "OrderClosure",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ProcessedOperation",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "SweeperLease",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockItem",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Warehouse",
                schema: "inventory");
        }
    }
}
