using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StubFulfillmentAndChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowStartAttempt_Result",
                schema: "orders",
                table: "WorkflowStartAttempt");

            migrationBuilder.CreateTable(
                name: "OrderCheck",
                schema: "orders",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CheckName = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Replica = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    TotalMinor = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    RiskScore = table.Column<int>(type: "int", nullable: true),
                    ReviewRequired = table.Column<bool>(type: "bit", nullable: true),
                    RuleVersion = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Replays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderCheck", x => new { x.OrderId, x.CheckName });
                    table.CheckConstraint("CK_OrderCheck_Name", "[CheckName] IN ('price', 'risk')");
                    table.CheckConstraint("CK_OrderCheck_Replays", "[Replays] >= 0");
                    table.ForeignKey(
                        name: "FK_OrderCheck_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StubEffect",
                schema: "orders",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Step = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Qualifier = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    AppliedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Replays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StubEffect", x => x.OperationKey);
                    table.CheckConstraint("CK_StubEffect_Replays", "[Replays] >= 0");
                    table.CheckConstraint("CK_StubEffect_Step", "[Step] LIKE 'stub-%'");
                    table.ForeignKey(
                        name: "FK_StubEffect_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowStartAttempt_Result",
                schema: "orders",
                table: "WorkflowStartAttempt",
                sql: "[Result] IS NULL OR [Result] IN ('Started', 'AlreadyExists', 'Ambiguous', 'Rejected', 'LeaseLost', 'AlreadyClosed', 'Unexpected')");

            migrationBuilder.CreateIndex(
                name: "UX_OrderCheck_OperationKey",
                schema: "orders",
                table: "OrderCheck",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StubEffect_Order",
                schema: "orders",
                table: "StubEffect",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderCheck",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "StubEffect",
                schema: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowStartAttempt_Result",
                schema: "orders",
                table: "WorkflowStartAttempt");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowStartAttempt_Result",
                schema: "orders",
                table: "WorkflowStartAttempt",
                sql: "[Result] IS NULL OR [Result] IN ('Started', 'AlreadyExists', 'Ambiguous', 'Rejected', 'LeaseLost')");
        }
    }
}
