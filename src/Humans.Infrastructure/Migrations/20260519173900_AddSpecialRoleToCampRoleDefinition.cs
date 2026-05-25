using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSpecialRoleToCampRoleDefinition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpecialRole",
                table: "camp_role_definitions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValueSql: "'None'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpecialRole",
                table: "camp_role_definitions");
        }
    }
}
