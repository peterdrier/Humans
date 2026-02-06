using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Profiles.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DropGoogleResourceGoogleIdUniqueConstraint : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_google_resources_GoogleId",
            table: "google_resources");

        migrationBuilder.CreateIndex(
            name: "IX_google_resources_GoogleId",
            table: "google_resources",
            column: "GoogleId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_google_resources_GoogleId",
            table: "google_resources");

        migrationBuilder.CreateIndex(
            name: "IX_google_resources_GoogleId",
            table: "google_resources",
            column: "GoogleId",
            unique: true);
    }
}
