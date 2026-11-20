using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InterventionRepairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepairStartRequest",
                schema: "orders",
                columns: table => new
                {
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<string>(type: "varchar(80)", unicode: false, maxLength: 80, nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    LeaseOwner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairStartRequest", x => x.CaseId);
                    table.CheckConstraint("CK_RepairStartRequest_Lease", "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");
                    table.CheckConstraint("CK_RepairStartRequest_State", "[State] IN ('Pending', 'Started', 'Closed')");
                    table.ForeignKey(
                        name: "FK_RepairStartRequest_InterventionCase_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "orders",
                        principalTable: "InterventionCase",
                        principalColumn: "CaseId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepairStartRequest_InstanceId",
                schema: "orders",
                table: "RepairStartRequest",
                column: "InstanceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepairStartRequest",
                schema: "orders");
        }
    }
}
