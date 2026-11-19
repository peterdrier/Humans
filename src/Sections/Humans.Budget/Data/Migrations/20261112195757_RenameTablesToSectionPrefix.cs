using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Budget.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ticketing_projections_budget_groups_BudgetGroupId",
                table: "ticketing_projections");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ticketing_projections",
                table: "ticketing_projections");

            migrationBuilder.RenameTable(
                name: "ticketing_projections",
                newName: "budget_ticketing_projections");

            migrationBuilder.RenameIndex(
                name: "IX_ticketing_projections_BudgetGroupId",
                table: "budget_ticketing_projections",
                newName: "IX_budget_ticketing_projections_BudgetGroupId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_budget_ticketing_projections",
                table: "budget_ticketing_projections",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_budget_ticketing_projections_budget_groups_BudgetGroupId",
                table: "budget_ticketing_projections",
                column: "BudgetGroupId",
                principalTable: "budget_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_budget_ticketing_projections_budget_groups_BudgetGroupId",
                table: "budget_ticketing_projections");

            migrationBuilder.DropPrimaryKey(
                name: "PK_budget_ticketing_projections",
                table: "budget_ticketing_projections");

            migrationBuilder.RenameTable(
                name: "budget_ticketing_projections",
                newName: "ticketing_projections");

            migrationBuilder.RenameIndex(
                name: "IX_budget_ticketing_projections_BudgetGroupId",
                table: "ticketing_projections",
                newName: "IX_ticketing_projections_BudgetGroupId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ticketing_projections",
                table: "ticketing_projections",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ticketing_projections_budget_groups_BudgetGroupId",
                table: "ticketing_projections",
                column: "BudgetGroupId",
                principalTable: "budget_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
