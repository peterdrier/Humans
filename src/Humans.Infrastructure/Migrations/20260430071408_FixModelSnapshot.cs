using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_camp_role_assignments_CampRoleDefinition_CampRoleDefinition~",
                table: "camp_role_assignments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CampRoleDefinition_TempId1",
                table: "CampRoleDefinition");

            migrationBuilder.RenameTable(
                name: "CampRoleDefinition",
                newName: "camp_role_definitions");

            migrationBuilder.RenameColumn(
                name: "TempId1",
                table: "camp_role_definitions",
                newName: "Id");

            migrationBuilder.AddColumn<Instant>(
                name: "CreatedAt",
                table: "camp_role_definitions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L));

            migrationBuilder.AddColumn<Instant>(
                name: "DeactivatedAt",
                table: "camp_role_definitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "camp_role_definitions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinimumRequired",
                table: "camp_role_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "camp_role_definitions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SlotCount",
                table: "camp_role_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "camp_role_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Instant>(
                name: "UpdatedAt",
                table: "camp_role_definitions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L));

            migrationBuilder.AddPrimaryKey(
                name: "PK_camp_role_definitions",
                table: "camp_role_definitions",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_definitions_name_unique",
                table: "camp_role_definitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_definitions_SortOrder",
                table: "camp_role_definitions",
                column: "SortOrder");

            migrationBuilder.AddForeignKey(
                name: "FK_camp_role_assignments_camp_role_definitions_CampRoleDefinit~",
                table: "camp_role_assignments",
                column: "CampRoleDefinitionId",
                principalTable: "camp_role_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_camp_role_assignments_camp_role_definitions_CampRoleDefinit~",
                table: "camp_role_assignments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_camp_role_definitions",
                table: "camp_role_definitions");

            migrationBuilder.DropIndex(
                name: "IX_camp_role_definitions_name_unique",
                table: "camp_role_definitions");

            migrationBuilder.DropIndex(
                name: "IX_camp_role_definitions_SortOrder",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "DeactivatedAt",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "MinimumRequired",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "SlotCount",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "camp_role_definitions");

            migrationBuilder.RenameTable(
                name: "camp_role_definitions",
                newName: "CampRoleDefinition");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "CampRoleDefinition",
                newName: "TempId1");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CampRoleDefinition_TempId1",
                table: "CampRoleDefinition",
                column: "TempId1");

            migrationBuilder.AddForeignKey(
                name: "FK_camp_role_assignments_CampRoleDefinition_CampRoleDefinition~",
                table: "camp_role_assignments",
                column: "CampRoleDefinitionId",
                principalTable: "CampRoleDefinition",
                principalColumn: "TempId1",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
