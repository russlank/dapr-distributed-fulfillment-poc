using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleCancellationAndCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CancelRequestedAt",
                schema: "orders",
                table: "Order",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchStartedAt",
                schema: "orders",
                table: "Order",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InterventionCase",
                schema: "orders",
                columns: table => new
                {
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWSEQUENTIALID()"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    OperationKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    Evidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ResumeStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ResolutionAction = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    ResolutionPath = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    ResolutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResolutionEvidence = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResolvedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RepairOutcome = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterventionCase", x => x.CaseId);
                    table.CheckConstraint("CK_InterventionCase_Action", "[ResolutionAction] IS NULL OR [ResolutionAction] IN ('ConfirmExternalSuccess', 'ConfirmExternalFailure', 'Acknowledge')");
                    table.CheckConstraint("CK_InterventionCase_Kind", "[Kind] IN ('AmbiguousPayment', 'AmbiguousDispatch', 'CarrierTerminalConflict', 'DeliveryException', 'DeliveryOverdue', 'CompensationExhausted', 'WorkflowFaulted', 'WorkflowMissing', 'ExecutionMismatch', 'SignalStuck', 'ProjectionGap')");
                    table.CheckConstraint("CK_InterventionCase_Path", "[ResolutionPath] IS NULL OR [ResolutionPath] IN ('Signal', 'Repair', 'RecordOnly')");
                    table.CheckConstraint("CK_InterventionCase_Resolution", "([State] = 'Resolved' AND [ResolutionAction] IS NOT NULL AND [ResolutionPath] IS NOT NULL AND [ResolvedAt] IS NOT NULL) OR ([State] <> 'Resolved' AND [ResolutionAction] IS NULL AND [ResolutionPath] IS NULL AND [ResolvedAt] IS NULL)");
                    table.CheckConstraint("CK_InterventionCase_State", "[State] IN ('Open', 'Reconciling', 'Resolved', 'Abandoned')");
                    table.ForeignKey(
                        name: "FK_InterventionCase_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Order_CancelOrDispatch",
                schema: "orders",
                table: "Order",
                sql: "[CancelRequestedAt] IS NULL OR [DispatchStartedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_InterventionCase_OpenPerKind",
                schema: "orders",
                table: "InterventionCase",
                columns: new[] { "OrderId", "Kind" },
                unique: true,
                filter: "[State] IN ('Open', 'Reconciling')");

            migrationBuilder.CreateIndex(
                name: "UX_InterventionCase_OperationKey",
                schema: "orders",
                table: "InterventionCase",
                column: "OperationKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InterventionCase",
                schema: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Order_CancelOrDispatch",
                schema: "orders",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "CancelRequestedAt",
                schema: "orders",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "DispatchStartedAt",
                schema: "orders",
                table: "Order");
        }
    }
}
