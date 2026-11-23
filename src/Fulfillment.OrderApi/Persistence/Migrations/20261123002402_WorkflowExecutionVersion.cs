using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowExecutionVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExecutionVersion",
                schema: "orders",
                table: "WorkflowStartRequest",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionVersion",
                schema: "orders",
                table: "WorkflowStartRequest");
        }
    }
}
