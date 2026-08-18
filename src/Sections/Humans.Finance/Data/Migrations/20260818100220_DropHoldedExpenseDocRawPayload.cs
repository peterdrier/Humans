using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropHoldedExpenseDocRawPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RawPayload",
                table: "holded_expense_docs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RawPayload",
                table: "holded_expense_docs",
                type: "jsonb",
                nullable: false,
                defaultValue: "");
        }
    }
}
