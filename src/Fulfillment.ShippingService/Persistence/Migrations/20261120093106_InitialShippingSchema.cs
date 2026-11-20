using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.ShippingService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialShippingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "shipping");

            migrationBuilder.CreateTable(
                name: "Shipment",
                schema: "shipping",
                columns: table => new
                {
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Parcels = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CurrentStatus = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    TerminalStatus = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    TerminalEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CarrierAcceptedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeliveryDeadlineAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipment", x => x.ShipmentId);
                    table.CheckConstraint("CK_Shipment_CurrentStatus", "CurrentStatus IS NULL OR CurrentStatus IN ('Accepted', 'PickedUp', 'InTransit', 'Delivered', 'Exception')");
                    table.CheckConstraint("CK_Shipment_State", "State IN ('Prepared', 'Dispatched', 'Ambiguous')");
                    table.CheckConstraint("CK_Shipment_Terminal", "(TerminalStatus IS NULL AND TerminalEventId IS NULL) OR (TerminalStatus IS NOT NULL AND TerminalEventId IS NOT NULL)");
                    table.CheckConstraint("CK_Shipment_TerminalStatus", "TerminalStatus IS NULL OR TerminalStatus IN ('Delivered', 'Exception')");
                });

            migrationBuilder.CreateTable(
                name: "CarrierStatusEvent",
                schema: "shipping",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Source = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    PayloadHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Replays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarrierStatusEvent", x => x.EventId);
                    table.CheckConstraint("CK_CarrierStatusEvent_Outcome", "Outcome IN ('Applied', 'TerminalApplied', 'Stale', 'TerminalConflict', 'TerminalRepeat', 'CorrelationMismatch')");
                    table.CheckConstraint("CK_CarrierStatusEvent_Source", "Source IN ('callback', 'poll')");
                    table.CheckConstraint("CK_CarrierStatusEvent_Status", "Status IN ('Accepted', 'PickedUp', 'InTransit', 'Delivered', 'Exception')");
                    table.ForeignKey(
                        name: "FK_CarrierStatusEvent_Shipment_ShipmentId",
                        column: x => x.ShipmentId,
                        principalSchema: "shipping",
                        principalTable: "Shipment",
                        principalColumn: "ShipmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DispatchAttempt",
                schema: "shipping",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Answer = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Replica = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchAttempt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispatchAttempt_Shipment_ShipmentId",
                        column: x => x.ShipmentId,
                        principalSchema: "shipping",
                        principalTable: "Shipment",
                        principalColumn: "ShipmentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_CarrierStatusEvent_ShipmentVersion",
                schema: "shipping",
                table: "CarrierStatusEvent",
                columns: new[] { "ShipmentId", "Version" },
                unique: true,
                filter: "[Outcome] <> 'CorrelationMismatch'");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchAttempt_ShipmentId",
                schema: "shipping",
                table: "DispatchAttempt",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipment_OrderId",
                schema: "shipping",
                table: "Shipment",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarrierStatusEvent",
                schema: "shipping");

            migrationBuilder.DropTable(
                name: "DispatchAttempt",
                schema: "shipping");

            migrationBuilder.DropTable(
                name: "Shipment",
                schema: "shipping");
        }
    }
}
