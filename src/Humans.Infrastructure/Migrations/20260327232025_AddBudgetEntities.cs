using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasBudget",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "budget_years",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
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
                    table.ForeignKey(
                        name: "FK_budget_audit_logs_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
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
                    table.ForeignKey(
                        name: "FK_budget_categories_teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                    table.ForeignKey(
                        name: "FK_budget_line_items_teams_ResponsibleTeamId",
                        column: x => x.ResponsibleTeamId,
                        principalTable: "teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000001"),
                column: "HasBudget",
                value: false);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000002"),
                column: "HasBudget",
                value: false);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000003"),
                column: "HasBudget",
                value: false);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000004"),
                column: "HasBudget",
                value: false);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"),
                column: "HasBudget",
                value: false);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000006"),
                column: "HasBudget",
                value: false);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_audit_logs");

            migrationBuilder.DropTable(
                name: "budget_line_items");

            migrationBuilder.DropTable(
                name: "budget_categories");

            migrationBuilder.DropTable(
                name: "budget_groups");

            migrationBuilder.DropTable(
                name: "budget_years");

            migrationBuilder.DropColumn(
                name: "HasBudget",
                table: "teams");
        }
    }
}
