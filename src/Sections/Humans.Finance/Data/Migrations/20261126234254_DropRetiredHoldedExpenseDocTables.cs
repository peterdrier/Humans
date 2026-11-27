using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRetiredHoldedExpenseDocTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finance_holded_doc_sync_states");

            migrationBuilder.DropTable(
                name: "finance_holded_expense_docs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "finance_holded_doc_sync_states",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastSyncedDocCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_holded_doc_sync_states", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "finance_holded_expense_docs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BookedAccountId = table.Column<string>(type: "text", nullable: true),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DocNumber = table.Column<string>(type: "text", nullable: false),
                    HoldedDocId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: true),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    MatchSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MatchStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true),
                    Subtotal = table.Column<decimal>(type: "numeric", nullable: false),
                    TagsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric", nullable: false),
                    Total = table.Column<decimal>(type: "numeric", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_finance_holded_expense_docs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_finance_holded_expense_docs_BudgetCategoryId",
                table: "finance_holded_expense_docs",
                column: "BudgetCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_finance_holded_expense_docs_Date",
                table: "finance_holded_expense_docs",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_finance_holded_expense_docs_HoldedDocId",
                table: "finance_holded_expense_docs",
                column: "HoldedDocId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_finance_holded_expense_docs_MatchStatus",
                table: "finance_holded_expense_docs",
                column: "MatchStatus");
        }
    }
}
