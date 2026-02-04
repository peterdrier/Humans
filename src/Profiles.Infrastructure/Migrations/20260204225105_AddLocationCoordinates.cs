using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationCoordinates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "profiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "profiles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceId",
                table: "profiles",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "PlaceId",
                table: "profiles");
        }
    }
}
