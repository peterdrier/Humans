using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Grants.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGrantProgramEventIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EventSettingsId",
                table: "grant_programs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_grant_programs_EventSettingsId",
                table: "grant_programs",
                column: "EventSettingsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_grant_programs_EventSettingsId",
                table: "grant_programs");

            migrationBuilder.DropColumn(
                name: "EventSettingsId",
                table: "grant_programs");
        }
    }
}
