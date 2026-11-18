using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StartDispatchAndExecutionClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowStartRequest_Lease",
                schema: "orders",
                table: "WorkflowStartRequest");

            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                schema: "orders",
                table: "WorkflowStartRequest",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExecutionClaim",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    OrderStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionClaim", x => x.Id);
                    table.CheckConstraint("CK_ExecutionClaim_Outcome", "[Outcome] IN ('Proceed', 'AlreadyTerminal')");
                    table.ForeignKey(
                        name: "FK_ExecutionClaim_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowStartAttempt",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatcherId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    InstanceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    CalledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Result = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    ReportedStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStartAttempt", x => x.Id);
                    table.CheckConstraint("CK_WorkflowStartAttempt_Result", "[Result] IS NULL OR [Result] IN ('Started', 'AlreadyExists', 'Ambiguous', 'Rejected', 'LeaseLost')");
                    table.ForeignKey(
                        name: "FK_WorkflowStartAttempt_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowStartRequest_Lease",
                schema: "orders",
                table: "WorkflowStartRequest",
                sql: "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionClaim_Order",
                schema: "orders",
                table: "ExecutionClaim",
                columns: new[] { "OrderId", "ClaimedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStartAttempt_Order",
                schema: "orders",
                table: "WorkflowStartAttempt",
                columns: new[] { "OrderId", "CalledAt" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowStartAttempt_LeaseToken",
                schema: "orders",
                table: "WorkflowStartAttempt",
                column: "LeaseToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionClaim",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "WorkflowStartAttempt",
                schema: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowStartRequest_Lease",
                schema: "orders",
                table: "WorkflowStartRequest");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                schema: "orders",
                table: "WorkflowStartRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowStartRequest_Lease",
                schema: "orders",
                table: "WorkflowStartRequest",
                sql: "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL)");
        }
    }
}
