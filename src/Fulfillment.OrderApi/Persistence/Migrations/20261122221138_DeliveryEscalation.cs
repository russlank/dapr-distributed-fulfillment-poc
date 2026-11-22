using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderStatusHistory_ToStatus",
                schema: "orders",
                table: "OrderStatusHistory");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Order_Status",
                schema: "orders",
                table: "Order");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InterventionCase_State",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedAt",
                schema: "orders",
                table: "InterventionCase",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalatedBy",
                schema: "orders",
                table: "InterventionCase",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalationEvidence",
                schema: "orders",
                table: "InterventionCase",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EscalationId",
                schema: "orders",
                table: "InterventionCase",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderStatusHistory_ToStatus",
                schema: "orders",
                table: "OrderStatusHistory",
                sql: "[ToStatus] IN ('Submitted', 'Validating', 'Reserving', 'PaymentAuthorized', 'Packing', 'AwaitingApproval', 'Dispatched', 'AwaitingDelivery', 'Completed', 'Rejected', 'Cancelled', 'Compensated', 'ManualInterventionRequired', 'Escalated')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Order_Status",
                schema: "orders",
                table: "Order",
                sql: "[Status] IN ('Submitted', 'Validating', 'Reserving', 'PaymentAuthorized', 'Packing', 'AwaitingApproval', 'Dispatched', 'AwaitingDelivery', 'Completed', 'Rejected', 'Cancelled', 'Compensated', 'ManualInterventionRequired', 'Escalated')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InterventionCase_Escalation",
                schema: "orders",
                table: "InterventionCase",
                sql: "([EscalationId] IS NULL AND [EscalatedAt] IS NULL AND [State] <> 'Escalated') OR ([EscalationId] IS NOT NULL AND [EscalatedAt] IS NOT NULL AND [State] IN ('Escalated', 'Resolved'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InterventionCase_State",
                schema: "orders",
                table: "InterventionCase",
                sql: "[State] IN ('Open', 'Reconciling', 'Escalated', 'Resolved', 'Abandoned')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderStatusHistory_ToStatus",
                schema: "orders",
                table: "OrderStatusHistory");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Order_Status",
                schema: "orders",
                table: "Order");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InterventionCase_Escalation",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InterventionCase_State",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.DropColumn(
                name: "EscalatedBy",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.DropColumn(
                name: "EscalationEvidence",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.DropColumn(
                name: "EscalationId",
                schema: "orders",
                table: "InterventionCase");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderStatusHistory_ToStatus",
                schema: "orders",
                table: "OrderStatusHistory",
                sql: "[ToStatus] IN ('Submitted', 'Validating', 'Reserving', 'PaymentAuthorized', 'Packing', 'AwaitingApproval', 'Dispatched', 'AwaitingDelivery', 'Completed', 'Rejected', 'Cancelled', 'Compensated', 'ManualInterventionRequired')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Order_Status",
                schema: "orders",
                table: "Order",
                sql: "[Status] IN ('Submitted', 'Validating', 'Reserving', 'PaymentAuthorized', 'Packing', 'AwaitingApproval', 'Dispatched', 'AwaitingDelivery', 'Completed', 'Rejected', 'Cancelled', 'Compensated', 'ManualInterventionRequired')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InterventionCase_State",
                schema: "orders",
                table: "InterventionCase",
                sql: "[State] IN ('Open', 'Reconciling', 'Resolved', 'Abandoned')");
        }
    }
}
