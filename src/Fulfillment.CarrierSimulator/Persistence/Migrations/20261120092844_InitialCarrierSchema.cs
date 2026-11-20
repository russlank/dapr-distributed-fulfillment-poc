using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.CarrierSimulator.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCarrierSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "carrier");

            migrationBuilder.CreateTable(
                name: "Behavior",
                schema: "carrier",
                columns: table => new
                {
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Behavior", x => x.ShipmentId);
                    table.CheckConstraint("CK_Behavior_Mode", "Mode IN ('Auto', 'Manual')");
                });

            migrationBuilder.CreateTable(
                name: "Callback",
                schema: "carrier",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastResponse = table.Column<int>(type: "int", nullable: true),
                    IsTruth = table.Column<bool>(type: "bit", nullable: false),
                    BadSignature = table.Column<bool>(type: "bit", nullable: false),
                    Label = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Callback", x => x.Id);
                    table.CheckConstraint("CK_Callback_State", "State IN ('Scheduled', 'Sent', 'DeadLettered')");
                    table.CheckConstraint("CK_Callback_Status", "Status IN ('Accepted', 'PickedUp', 'InTransit', 'Delivered', 'Exception')");
                });

            migrationBuilder.CreateTable(
                name: "Send",
                schema: "carrier",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CallbackId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Target = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    ResponseCode = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Send", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shipment",
                schema: "carrier",
                columns: table => new
                {
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Parcels = table.Column<int>(type: "int", nullable: false),
                    PayloadHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Replays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipment", x => x.ShipmentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Callback_ShipmentId",
                schema: "carrier",
                table: "Callback",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Callback_State_DueAt",
                schema: "carrier",
                table: "Callback",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Send_CallbackId",
                schema: "carrier",
                table: "Send",
                column: "CallbackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Behavior",
                schema: "carrier");

            migrationBuilder.DropTable(
                name: "Callback",
                schema: "carrier");

            migrationBuilder.DropTable(
                name: "Send",
                schema: "carrier");

            migrationBuilder.DropTable(
                name: "Shipment",
                schema: "carrier");
        }
    }
}
