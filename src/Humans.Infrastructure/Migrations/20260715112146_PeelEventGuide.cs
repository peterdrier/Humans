using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the EventGuide peel
    /// (nobodies-collective/Humans#858): <c>events</c>, <c>event_categories</c>,
    /// <c>event_venues</c>, <c>event_guide_settings</c>,
    /// <c>event_moderation_actions</c>, <c>event_favourites</c> and
    /// <c>event_preferences</c> moved to <c>EventGuideDbContext</c>, which owns the
    /// physical tables from here on. The scaffolded
    /// <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately emptied —
    /// Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelEventGuide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
