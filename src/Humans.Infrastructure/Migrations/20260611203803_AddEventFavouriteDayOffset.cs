using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventFavouriteDayOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_event_favourites_UserId_GuideEventId",
                table: "event_favourites");

            migrationBuilder.AddColumn<int>(
                name: "DayOffset",
                table: "event_favourites",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_event_favourites_UserId_GuideEventId_DayOffset",
                table: "event_favourites",
                columns: new[] { "UserId", "GuideEventId", "DayOffset" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_event_favourites_UserId_GuideEventId_DayOffset",
                table: "event_favourites");

            migrationBuilder.DropColumn(
                name: "DayOffset",
                table: "event_favourites");

            migrationBuilder.CreateIndex(
                name: "IX_event_favourites_UserId_GuideEventId",
                table: "event_favourites",
                columns: new[] { "UserId", "GuideEventId" },
                unique: true);
        }
    }
}
