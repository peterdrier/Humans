using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExpensesSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Iban",
                table: "profiles",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "expense_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Extension = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "expense_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmitterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PayeeName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayeeIban = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    SubmittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CoordinatorEndorsedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoordinatorEndorsedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SepaSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastRejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastRejectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRejectedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    HoldedDocId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "holded_expense_outbox_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpenseReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailedPermanently = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_expense_outbox_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "expense_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpenseReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_expense_lines_expense_attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "expense_attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_expense_lines_expense_reports_ExpenseReportId",
                        column: x => x.ExpenseReportId,
                        principalTable: "expense_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_expense_lines_AttachmentId",
                table: "expense_lines",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_expense_lines_ExpenseReportId",
                table: "expense_lines",
                column: "ExpenseReportId");

            migrationBuilder.CreateIndex(
                name: "IX_expense_reports_BudgetCategoryId",
                table: "expense_reports",
                column: "BudgetCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_expense_reports_HoldedDocId",
                table: "expense_reports",
                column: "HoldedDocId");

            migrationBuilder.CreateIndex(
                name: "IX_expense_reports_Status",
                table: "expense_reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_expense_reports_SubmitterUserId_Status",
                table: "expense_reports",
                columns: new[] { "SubmitterUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_holded_expense_outbox_events_ExpenseReportId",
                table: "holded_expense_outbox_events",
                column: "ExpenseReportId");

            migrationBuilder.CreateIndex(
                name: "IX_holded_expense_outbox_events_ProcessedAt_FailedPermanently",
                table: "holded_expense_outbox_events",
                columns: new[] { "ProcessedAt", "FailedPermanently" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expense_lines");

            migrationBuilder.DropTable(
                name: "holded_expense_outbox_events");

            migrationBuilder.DropTable(
                name: "expense_attachments");

            migrationBuilder.DropTable(
                name: "expense_reports");

            migrationBuilder.DropColumn(
                name: "Iban",
                table: "profiles");
        }
    }
}
