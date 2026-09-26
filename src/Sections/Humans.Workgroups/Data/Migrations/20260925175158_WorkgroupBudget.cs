using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Workgroups.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkgroupBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "BudgetAmount",
                table: "workgroups",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldedAccountId",
                table: "workgroups",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HoldedAccountNumber",
                table: "workgroups",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BudgetAmount",
                table: "workgroups");

            migrationBuilder.DropColumn(
                name: "HoldedAccountId",
                table: "workgroups");

            migrationBuilder.DropColumn(
                name: "HoldedAccountNumber",
                table: "workgroups");
        }
    }
}
