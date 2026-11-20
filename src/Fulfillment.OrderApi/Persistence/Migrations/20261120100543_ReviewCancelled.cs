using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewCancelled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewTask_Lifecycle",
                schema: "orders",
                table: "ReviewTask");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewTask_Status",
                schema: "orders",
                table: "ReviewTask");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewTask_Lifecycle",
                schema: "orders",
                table: "ReviewTask",
                sql: "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecisionId] IS NULL AND [ExpiredAt] IS NULL) OR ([Status] IN ('Approved', 'Rejected') AND [DecidedAt] IS NOT NULL AND [DecisionId] IS NOT NULL AND [ExpiredAt] IS NULL) OR ([Status] = 'Expired' AND [ExpiredAt] IS NOT NULL AND [DecidedAt] IS NULL AND [DecisionId] IS NULL) OR ([Status] = 'Cancelled' AND [DecidedAt] IS NULL AND [DecisionId] IS NULL AND [ExpiredAt] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewTask_Status",
                schema: "orders",
                table: "ReviewTask",
                sql: "[Status] IN ('Pending', 'Approved', 'Rejected', 'Expired', 'Cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewTask_Lifecycle",
                schema: "orders",
                table: "ReviewTask");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReviewTask_Status",
                schema: "orders",
                table: "ReviewTask");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewTask_Lifecycle",
                schema: "orders",
                table: "ReviewTask",
                sql: "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecisionId] IS NULL AND [ExpiredAt] IS NULL) OR ([Status] IN ('Approved', 'Rejected') AND [DecidedAt] IS NOT NULL AND [DecisionId] IS NOT NULL AND [ExpiredAt] IS NULL) OR ([Status] = 'Expired' AND [ExpiredAt] IS NOT NULL AND [DecidedAt] IS NULL AND [DecisionId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReviewTask_Status",
                schema: "orders",
                table: "ReviewTask",
                sql: "[Status] IN ('Pending', 'Approved', 'Rejected', 'Expired')");
        }
    }
}
