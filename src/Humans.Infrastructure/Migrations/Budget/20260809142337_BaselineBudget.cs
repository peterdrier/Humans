using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations.Budget
{
    /// <inheritdoc />
    public partial class BaselineBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budget_years",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_years", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "budget_audit_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OldValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NewValue = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_audit_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_audit_logs_budget_years_BudgetYearId",
                        column: x => x.BudgetYearId,
                        principalTable: "budget_years",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "budget_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetYearId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsRestricted = table.Column<bool>(type: "boolean", nullable: false),
                    IsDepartmentGroup = table.Column<bool>(type: "boolean", nullable: false),
                    IsTicketingGroup = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_groups_budget_years_BudgetYearId",
                        column: x => x.BudgetYearId,
                        principalTable: "budget_years",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "budget_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpenditureType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_categories_budget_groups_BudgetGroupId",
                        column: x => x.BudgetGroupId,
                        principalTable: "budget_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticketing_projections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartDate = table.Column<LocalDate>(type: "date", nullable: true),
                    EventDate = table.Column<LocalDate>(type: "date", nullable: true),
                    InitialSalesCount = table.Column<int>(type: "integer", nullable: false),
                    DailySalesRate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AverageTicketPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRate = table.Column<int>(type: "integer", nullable: false),
                    StripeFeePercent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StripeFeeFixed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TicketTailorFeePercent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticketing_projections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticketing_projections_budget_groups_BudgetGroupId",
                        column: x => x.BudgetGroupId,
                        principalTable: "budget_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "budget_line_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ResponsibleTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ExpectedDate = table.Column<LocalDate>(type: "date", nullable: true),
                    VatRate = table.Column<int>(type: "integer", nullable: false),
                    IsAutoGenerated = table.Column<bool>(type: "boolean", nullable: false),
                    IsCashflowOnly = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_line_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budget_line_items_budget_categories_BudgetCategoryId",
                        column: x => x.BudgetCategoryId,
                        principalTable: "budget_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_ActorUserId",
                table: "budget_audit_logs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_BudgetYearId",
                table: "budget_audit_logs",
                column: "BudgetYearId");

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_EntityType_EntityId",
                table: "budget_audit_logs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_audit_logs_OccurredAt",
                table: "budget_audit_logs",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_budget_categories_BudgetGroupId_SortOrder",
                table: "budget_categories",
                columns: new[] { "BudgetGroupId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_categories_TeamId",
                table: "budget_categories",
                column: "TeamId",
                filter: "\"TeamId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_budget_groups_BudgetYearId_SortOrder",
                table: "budget_groups",
                columns: new[] { "BudgetYearId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_line_items_BudgetCategoryId_SortOrder",
                table: "budget_line_items",
                columns: new[] { "BudgetCategoryId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_budget_line_items_ResponsibleTeamId",
                table: "budget_line_items",
                column: "ResponsibleTeamId",
                filter: "\"ResponsibleTeamId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_budget_years_Status",
                table: "budget_years",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_budget_years_Year",
                table: "budget_years",
                column: "Year",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticketing_projections_BudgetGroupId",
                table: "ticketing_projections",
                column: "BudgetGroupId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_audit_logs");

            migrationBuilder.DropTable(
                name: "budget_line_items");

            migrationBuilder.DropTable(
                name: "ticketing_projections");

            migrationBuilder.DropTable(
                name: "budget_categories");

            migrationBuilder.DropTable(
                name: "budget_groups");

            migrationBuilder.DropTable(
                name: "budget_years");
        }
    }
}
