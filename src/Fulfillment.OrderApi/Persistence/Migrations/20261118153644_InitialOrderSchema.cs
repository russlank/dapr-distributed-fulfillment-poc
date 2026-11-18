using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrderSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "Order",
                schema: "orders",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Order", x => x.OrderId);
                    table.CheckConstraint("CK_Order_Status", "[Status] IN ('Submitted', 'Validating', 'Reserving', 'PaymentAuthorized', 'Packing', 'AwaitingApproval', 'Dispatched', 'AwaitingDelivery', 'Completed', 'Rejected', 'Cancelled', 'Compensated', 'ManualInterventionRequired')");
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyKey",
                schema: "orders",
                columns: table => new
                {
                    Key = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    RequestHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyKey", x => x.Key);
                    table.CheckConstraint("CK_IdempotencyKey_Hash", "DATALENGTH([RequestHash]) = 32");
                    table.ForeignKey(
                        name: "FK_IdempotencyKey_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderLine",
                schema: "orders",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    Sku = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLine", x => new { x.OrderId, x.LineNumber });
                    table.CheckConstraint("CK_OrderLine_LineNumber", "[LineNumber] BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_OrderLine_Quantity", "[Quantity] BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_OrderLine_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderStatusHistory",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    FromStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStatusHistory", x => x.Id);
                    table.CheckConstraint("CK_OrderStatusHistory_Sequence", "[Sequence] >= 1");
                    table.CheckConstraint("CK_OrderStatusHistory_ToStatus", "[ToStatus] IN ('Submitted', 'Validating', 'Reserving', 'PaymentAuthorized', 'Packing', 'AwaitingApproval', 'Dispatched', 'AwaitingDelivery', 'Completed', 'Rejected', 'Cancelled', 'Compensated', 'ManualInterventionRequired')");
                    table.ForeignKey(
                        name: "FK_OrderStatusHistory_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowStartRequest",
                schema: "orders",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ExecutionClaims = table.Column<int>(type: "int", nullable: false),
                    FenceRejections = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStartRequest", x => x.OrderId);
                    table.CheckConstraint("CK_WorkflowStartRequest_Counters", "[Attempts] >= 0 AND [ExecutionClaims] >= 0 AND [FenceRejections] >= 0");
                    table.CheckConstraint("CK_WorkflowStartRequest_Lease", "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL)");
                    table.CheckConstraint("CK_WorkflowStartRequest_Lifecycle", "([State] = 'Pending' AND [StartedAt] IS NULL AND [ClosedAt] IS NULL) OR ([State] = 'Started' AND [StartedAt] IS NOT NULL AND [ClosedAt] IS NULL) OR ([State] = 'Closed' AND [ClosedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_WorkflowStartRequest_State", "[State] IN ('Pending', 'Started', 'Closed')");
                    table.ForeignKey(
                        name: "FK_WorkflowStartRequest_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_IdempotencyKey_Order",
                schema: "orders",
                table: "IdempotencyKey",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Order_Customer",
                schema: "orders",
                table: "Order",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_OrderStatusHistory_Order_Sequence",
                schema: "orders",
                table: "OrderStatusHistory",
                columns: new[] { "OrderId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStartRequest_Due",
                schema: "orders",
                table: "WorkflowStartRequest",
                column: "NextAttemptAt",
                filter: "[State] = 'Pending'")
                .Annotation("SqlServer:Include", new[] { "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkflowStartRequest_Instance",
                schema: "orders",
                table: "WorkflowStartRequest",
                column: "InstanceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyKey",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OrderLine",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "OrderStatusHistory",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "WorkflowStartRequest",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "Order",
                schema: "orders");
        }
    }
}
