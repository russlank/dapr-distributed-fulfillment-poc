using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CarrierIngressAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CarrierCallbackConflict",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoredHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ReceivedHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    DetectedBy = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarrierCallbackConflict", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CarrierCallbackInbox",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Duplicates = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarrierCallbackInbox", x => x.Id);
                    table.CheckConstraint("CK_CarrierCallbackInbox_Status", "[Status] IN ('Accepted', 'PickedUp', 'InTransit', 'Delivered', 'Exception')");
                });

            migrationBuilder.CreateTable(
                name: "CarrierStatusForward",
                schema: "orders",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    LeaseOwner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarrierStatusForward", x => x.EventId);
                    table.CheckConstraint("CK_CarrierStatusForward_Lease", "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");
                    table.CheckConstraint("CK_CarrierStatusForward_Processed", "([State] = 'Processed' AND [Outcome] IS NOT NULL AND [ProcessedAt] IS NOT NULL AND [LeaseToken] IS NULL) OR ([State] = 'Pending' AND [Outcome] IS NULL AND [ProcessedAt] IS NULL)");
                    table.CheckConstraint("CK_CarrierStatusForward_State", "[State] IN ('Pending', 'Processed')");
                });

            migrationBuilder.CreateTable(
                name: "OrderOutbox",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderOutbox", x => x.Id);
                    table.CheckConstraint("CK_OrderOutbox_EventType", "[EventType] IN ('OrderCompleted')");
                    table.CheckConstraint("CK_OrderOutbox_State", "[State] IN ('Pending', 'Published')");
                    table.ForeignKey(
                        name: "FK_OrderOutbox_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CarrierCallbackInbox_ShipmentId",
                schema: "orders",
                table: "CarrierCallbackInbox",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "UX_CarrierCallbackInbox_Event",
                schema: "orders",
                table: "CarrierCallbackInbox",
                columns: new[] { "Source", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CarrierStatusForward_State_NextAttemptAt",
                schema: "orders",
                table: "CarrierStatusForward",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_OrderOutbox_Event",
                schema: "orders",
                table: "OrderOutbox",
                columns: new[] { "OrderId", "EventType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarrierCallbackConflict",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "CarrierCallbackInbox",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "CarrierStatusForward",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OrderOutbox",
                schema: "orders");
        }
    }
}
