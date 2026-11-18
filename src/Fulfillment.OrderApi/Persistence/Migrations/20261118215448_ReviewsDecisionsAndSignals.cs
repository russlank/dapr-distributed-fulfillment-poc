using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fulfillment.OrderApi.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReviewsDecisionsAndSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingSignal",
                schema: "orders",
                columns: table => new
                {
                    SignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    EventName = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    CausedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    LeaseOwner = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    ForwardedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingSignal", x => x.SignalId);
                    table.CheckConstraint("CK_PendingSignal_Counters", "[Attempts] >= 0");
                    table.CheckConstraint("CK_PendingSignal_Lease", "([LeaseOwner] IS NULL AND [LeaseExpiresAt] IS NULL AND [LeaseToken] IS NULL) OR ([LeaseOwner] IS NOT NULL AND [LeaseExpiresAt] IS NOT NULL AND [LeaseToken] IS NOT NULL)");
                    table.CheckConstraint("CK_PendingSignal_Lifecycle", "([State] = 'Pending' AND [ForwardedAt] IS NULL AND [ClosedAt] IS NULL) OR ([State] = 'Forwarded' AND [ForwardedAt] IS NOT NULL AND [LeaseToken] IS NULL) OR ([State] IN ('Discarded', 'Stuck') AND [ClosedAt] IS NOT NULL AND [LeaseToken] IS NULL)");
                    table.CheckConstraint("CK_PendingSignal_State", "[State] IN ('Pending', 'Forwarded', 'Discarded', 'Stuck')");
                    table.ForeignKey(
                        name: "FK_PendingSignal_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewTask",
                schema: "orders",
                columns: table => new
                {
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    DeadlineAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewTask", x => x.ReviewId);
                    table.CheckConstraint("CK_ReviewTask_Lifecycle", "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecisionId] IS NULL AND [ExpiredAt] IS NULL) OR ([Status] IN ('Approved', 'Rejected') AND [DecidedAt] IS NOT NULL AND [DecisionId] IS NOT NULL AND [ExpiredAt] IS NULL) OR ([Status] = 'Expired' AND [ExpiredAt] IS NOT NULL AND [DecidedAt] IS NULL AND [DecisionId] IS NULL)");
                    table.CheckConstraint("CK_ReviewTask_Purpose", "[Purpose] IN ('Approval')");
                    table.CheckConstraint("CK_ReviewTask_Round", "[Round] >= 1");
                    table.CheckConstraint("CK_ReviewTask_Status", "[Status] IN ('Pending', 'Approved', 'Rejected', 'Expired')");
                    table.ForeignKey(
                        name: "FK_ReviewTask_Order_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "orders",
                        principalTable: "Order",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignalAttempt",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatcherId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    CalledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Result = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    ReportedStatus = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    InvocationAttempts = table.Column<int>(type: "int", nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    TransportError = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalAttempt", x => x.Id);
                    table.CheckConstraint("CK_SignalAttempt_Result", "[Result] IS NULL OR [Result] IN ('Forwarded', 'Discarded', 'Stuck', 'NotStarted', 'NotRunning', 'Ambiguous', 'Rejected', 'LeaseLost', 'Unexpected')");
                    table.ForeignKey(
                        name: "FK_SignalAttempt_PendingSignal_SignalId",
                        column: x => x.SignalId,
                        principalSchema: "orders",
                        principalTable: "PendingSignal",
                        principalColumn: "SignalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewDecision",
                schema: "orders",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    OperatorId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PayloadHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewDecision", x => x.DecisionId);
                    table.CheckConstraint("CK_ReviewDecision_Hash", "DATALENGTH([PayloadHash]) = 32");
                    table.CheckConstraint("CK_ReviewDecision_Outcome", "[Outcome] IN ('Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_ReviewDecision_ReviewTask_ReviewId",
                        column: x => x.ReviewId,
                        principalSchema: "orders",
                        principalTable: "ReviewTask",
                        principalColumn: "ReviewId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewWake",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReviewId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Wake = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    SeenStatus = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    DeadlinePassed = table.Column<bool>(type: "bit", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewWake", x => x.Id);
                    table.CheckConstraint("CK_ReviewWake_Wake", "[Wake] IN ('Initial', 'Signal', 'Timer', 'Recheck')");
                    table.ForeignKey(
                        name: "FK_ReviewWake_ReviewTask_ReviewId",
                        column: x => x.ReviewId,
                        principalSchema: "orders",
                        principalTable: "ReviewTask",
                        principalColumn: "ReviewId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingSignal_Due",
                schema: "orders",
                table: "PendingSignal",
                column: "NextAttemptAt",
                filter: "[State] = 'Pending'")
                .Annotation("SqlServer:Include", new[] { "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingSignal_OrderId",
                schema: "orders",
                table: "PendingSignal",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "UX_PendingSignal_Cause",
                schema: "orders",
                table: "PendingSignal",
                columns: new[] { "CausedBy", "EventName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReviewDecision_Review",
                schema: "orders",
                table: "ReviewDecision",
                column: "ReviewId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReviewTask_Order_Purpose_Round",
                schema: "orders",
                table: "ReviewTask",
                columns: new[] { "OrderId", "Purpose", "Round" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewWake_Review",
                schema: "orders",
                table: "ReviewWake",
                columns: new[] { "ReviewId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_SignalAttempt_Signal",
                schema: "orders",
                table: "SignalAttempt",
                columns: new[] { "SignalId", "CalledAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SignalAttempt_LeaseToken",
                schema: "orders",
                table: "SignalAttempt",
                column: "LeaseToken",
                unique: true);

            // Accepted decisions are immutable audit records: the database refuses updates.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER orders.TR_ReviewDecision_Immutable ON orders.ReviewDecision AFTER UPDATE AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'review decisions are immutable', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS orders.TR_ReviewDecision_Immutable;");

            migrationBuilder.DropTable(
                name: "ReviewDecision",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "ReviewWake",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "SignalAttempt",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "ReviewTask",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "PendingSignal",
                schema: "orders");
        }
    }
}
