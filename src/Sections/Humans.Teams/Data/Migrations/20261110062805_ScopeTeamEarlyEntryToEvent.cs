using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Teams.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeTeamEarlyEntryToEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "team_early_entry_grants",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "team_early_entry_grants");
        }
    }
}
