using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropHoldedCategoryMapIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "holded_category_map");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "holded_category_map",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
