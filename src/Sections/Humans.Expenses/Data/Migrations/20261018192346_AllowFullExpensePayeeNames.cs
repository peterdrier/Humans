using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowFullExpensePayeeNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PayeeName",
                table: "expense_reports",
                type: "character varying(513)",
                maxLength: 513,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PayeeName",
                table: "expense_reports",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(513)",
                oldMaxLength: 513);
        }
    }
}
