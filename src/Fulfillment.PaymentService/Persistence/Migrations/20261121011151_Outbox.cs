using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.PaymentService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Outbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "payment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    Payload = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, defaultValue: "Pending"),
                    Attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    LeaseOwner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Generation = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Stalled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    PublishedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                    table.CheckConstraint("CK_Outbox_EventType", "[EventType] IN ('PaymentOperationRecorded')");
                    table.CheckConstraint("CK_Outbox_Sequence", "[Sequence] >= 1");
                    table.CheckConstraint("CK_Outbox_State", "[State] IN ('Pending', 'Published')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Due",
                schema: "payment",
                table: "Outbox",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Outbox_EventId",
                schema: "payment",
                table: "Outbox",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Outbox_Order_Sequence",
                schema: "payment",
                table: "Outbox",
                columns: new[] { "OrderId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "payment");
        }
    }
}
