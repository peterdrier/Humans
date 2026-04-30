using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestrictModerationActionCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_moderation_actions_guide_events_GuideEventId",
                table: "moderation_actions");

            migrationBuilder.AddForeignKey(
                name: "FK_moderation_actions_guide_events_GuideEventId",
                table: "moderation_actions",
                column: "GuideEventId",
                principalTable: "guide_events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_moderation_actions_guide_events_GuideEventId",
                table: "moderation_actions");

            migrationBuilder.AddForeignKey(
                name: "FK_moderation_actions_guide_events_GuideEventId",
                table: "moderation_actions",
                column: "GuideEventId",
                principalTable: "guide_events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
