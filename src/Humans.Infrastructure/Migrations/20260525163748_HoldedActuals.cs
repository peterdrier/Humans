using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HoldedActuals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holded_category_map",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldedAccountNumber = table.Column<int>(type: "integer", nullable: false),
                    HoldedAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ArchivedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_category_map", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "holded_expense_docs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldedDocId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocNumber = table.Column<string>(type: "text", nullable: false),
                    ContactName = table.Column<string>(type: "text", nullable: false),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric", nullable: false),
                    Total = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ApprovedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    TagsJson = table.Column<string>(type: "jsonb", nullable: false),
                    BookedAccountId = table.Column<string>(type: "text", nullable: true),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MatchSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RawPayload = table.Column<string>(type: "jsonb", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_expense_docs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "holded_sync_states",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SyncStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastSyncedDocCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_sync_states", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "holded_sync_states",
                columns: new[] { "Id", "LastError", "LastSyncAt", "LastSyncedDocCount", "StatusChangedAt", "SyncStatus" },
                values: new object[] { 1, null, null, 0, null, "Idle" });

            migrationBuilder.CreateIndex(
                name: "IX_holded_category_map_BudgetCategoryId",
                table: "holded_category_map",
                column: "BudgetCategoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holded_category_map_HoldedAccountNumber",
                table: "holded_category_map",
                column: "HoldedAccountNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holded_expense_docs_BudgetCategoryId",
                table: "holded_expense_docs",
                column: "BudgetCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_holded_expense_docs_Date",
                table: "holded_expense_docs",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_holded_expense_docs_HoldedDocId",
                table: "holded_expense_docs",
                column: "HoldedDocId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holded_expense_docs_MatchStatus",
                table: "holded_expense_docs",
                column: "MatchStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_category_map");

            migrationBuilder.DropTable(
                name: "holded_expense_docs");

            migrationBuilder.DropTable(
                name: "holded_sync_states");
        }
    }
}
