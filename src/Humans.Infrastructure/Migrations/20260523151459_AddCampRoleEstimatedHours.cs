using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampRoleEstimatedHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedHours",
                table: "camp_role_definitions",
                type: "numeric(6,2)",
                precision: 6,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EstimatedHours",
                table: "camp_role_definitions");
        }
    }
}
