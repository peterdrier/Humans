using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Budget.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameBudgetChangesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_budget_audit_logs_budget_years_BudgetYearId",
                table: "budget_audit_logs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_budget_audit_logs",
                table: "budget_audit_logs");

            migrationBuilder.RenameTable(
                name: "budget_audit_logs",
                newName: "budget_changes");

            migrationBuilder.RenameIndex(
                name: "IX_budget_audit_logs_OccurredAt",
                table: "budget_changes",
                newName: "IX_budget_changes_OccurredAt");

            migrationBuilder.RenameIndex(
                name: "IX_budget_audit_logs_EntityType_EntityId",
                table: "budget_changes",
                newName: "IX_budget_changes_EntityType_EntityId");

            migrationBuilder.RenameIndex(
                name: "IX_budget_audit_logs_BudgetYearId",
                table: "budget_changes",
                newName: "IX_budget_changes_BudgetYearId");

            migrationBuilder.RenameIndex(
                name: "IX_budget_audit_logs_ActorUserId",
                table: "budget_changes",
                newName: "IX_budget_changes_ActorUserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_budget_changes",
                table: "budget_changes",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_budget_changes_budget_years_BudgetYearId",
                table: "budget_changes",
                column: "BudgetYearId",
                principalTable: "budget_years",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_budget_changes_budget_years_BudgetYearId",
                table: "budget_changes");

            migrationBuilder.DropPrimaryKey(
                name: "PK_budget_changes",
                table: "budget_changes");

            migrationBuilder.RenameTable(
                name: "budget_changes",
                newName: "budget_audit_logs");

            migrationBuilder.RenameIndex(
                name: "IX_budget_changes_OccurredAt",
                table: "budget_audit_logs",
                newName: "IX_budget_audit_logs_OccurredAt");

            migrationBuilder.RenameIndex(
                name: "IX_budget_changes_EntityType_EntityId",
                table: "budget_audit_logs",
                newName: "IX_budget_audit_logs_EntityType_EntityId");

            migrationBuilder.RenameIndex(
                name: "IX_budget_changes_BudgetYearId",
                table: "budget_audit_logs",
                newName: "IX_budget_audit_logs_BudgetYearId");

            migrationBuilder.RenameIndex(
                name: "IX_budget_changes_ActorUserId",
                table: "budget_audit_logs",
                newName: "IX_budget_audit_logs_ActorUserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_budget_audit_logs",
                table: "budget_audit_logs",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_budget_audit_logs_budget_years_BudgetYearId",
                table: "budget_audit_logs",
                column: "BudgetYearId",
                principalTable: "budget_years",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
