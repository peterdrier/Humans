using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldedAndBudgetSlugs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "budget_groups",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "budget_categories",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "holded_sync_states",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SyncStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LastSyncedDocCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_sync_states", x => x.Id);
                });

            // Seed singleton HoldedSyncState row (Id = 1).
            migrationBuilder.Sql(@"
                INSERT INTO holded_sync_states
                    (""Id"", ""SyncStatus"", ""StatusChangedAt"", ""LastSyncedDocCount"")
                VALUES
                    (1, 'Idle', NOW() AT TIME ZONE 'UTC', 0)
                ON CONFLICT (""Id"") DO NOTHING;
            ");

            migrationBuilder.CreateTable(
                name: "holded_transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldedDocId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HoldedDocNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContactName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    AccountingDate = table.Column<LocalDate>(type: "date", nullable: true),
                    DueDate = table.Column<LocalDate>(type: "date", nullable: true),
                    Subtotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentsTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentsPending = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaymentsRefunds = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ApprovedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Tags = table.Column<string>(type: "jsonb", nullable: false),
                    RawPayload = table.Column<string>(type: "jsonb", nullable: false),
                    SourceIncomingDocId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    MatchStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_holded_transactions_budget_categories_BudgetCategoryId",
                        column: x => x.BudgetCategoryId,
                        principalTable: "budget_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Backfill BudgetGroup slugs from Name (lower, accent-strip, dash-collapse, trim).
            // Must run before the unique index on (BudgetYearId, Slug) is created so existing
            // rows don't collide on empty default values.
            migrationBuilder.Sql(@"
                UPDATE budget_groups
                SET ""Slug"" = trim(both '-' from regexp_replace(
                    regexp_replace(
                        translate(lower(""Name""),
                            'áéíóúüñàèìòùâêîôûäëïöÿç',
                            'aeiouunaeiouaeiouaeioyc'),
                        '[^a-z0-9]+', '-', 'g'),
                    '-+', '-', 'g'))
                WHERE ""Slug"" IS NULL OR ""Slug"" = '';
            ");

            // Same for BudgetCategory.
            migrationBuilder.Sql(@"
                UPDATE budget_categories
                SET ""Slug"" = trim(both '-' from regexp_replace(
                    regexp_replace(
                        translate(lower(""Name""),
                            'áéíóúüñàèìòùâêîôûäëïöÿç',
                            'aeiouunaeiouaeiouaeioyc'),
                        '[^a-z0-9]+', '-', 'g'),
                    '-+', '-', 'g'))
                WHERE ""Slug"" IS NULL OR ""Slug"" = '';
            ");

            migrationBuilder.CreateIndex(
                name: "IX_budget_groups_BudgetYearId_Slug",
                table: "budget_groups",
                columns: new[] { "BudgetYearId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_budget_categories_BudgetGroupId_Slug",
                table: "budget_categories",
                columns: new[] { "BudgetGroupId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holded_transactions_BudgetCategoryId",
                table: "holded_transactions",
                column: "BudgetCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_holded_transactions_HoldedDocId",
                table: "holded_transactions",
                column: "HoldedDocId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holded_transactions_MatchStatus",
                table: "holded_transactions",
                column: "MatchStatus");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_sync_states");

            migrationBuilder.DropTable(
                name: "holded_transactions");

            migrationBuilder.DropIndex(
                name: "IX_budget_groups_BudgetYearId_Slug",
                table: "budget_groups");

            migrationBuilder.DropIndex(
                name: "IX_budget_categories_BudgetGroupId_Slug",
                table: "budget_categories");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "budget_groups");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "budget_categories");
        }
    }
}
