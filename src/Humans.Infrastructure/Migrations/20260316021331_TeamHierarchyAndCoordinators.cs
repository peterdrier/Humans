using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TeamHierarchyAndCoordinators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename string-stored enum values before schema changes
            migrationBuilder.Sql("UPDATE team_members SET \"Role\" = 'Coordinator' WHERE \"Role\" = 'Lead'");
            migrationBuilder.Sql("UPDATE teams SET \"SystemTeamType\" = 'Coordinators' WHERE \"SystemTeamType\" = 'Leads'");
            migrationBuilder.Sql("UPDATE contact_fields SET \"Visibility\" = 'CoordinatorsAndBoard' WHERE \"Visibility\" = 'LeadsAndBoard'");
            migrationBuilder.Sql("UPDATE user_emails SET \"Visibility\" = 'CoordinatorsAndBoard' WHERE \"Visibility\" = 'LeadsAndBoard'");

            // Rename system team
            migrationBuilder.Sql(@"
                UPDATE teams
                SET ""Name"" = 'Coordinators', ""Slug"" = 'coordinators', ""Description"" = 'All team coordinators'
                WHERE ""Id"" = '00000000-0000-0000-0001-000000000002'");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentTeamId",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManagement",
                table: "team_role_definitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000001"),
                column: "ParentTeamId",
                value: null);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000002"),
                columns: new[] { "Description", "Name", "ParentTeamId", "Slug", "SystemTeamType" },
                values: new object[] { "All team coordinators", "Coordinators", null, "coordinators", "Coordinators" });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000003"),
                column: "ParentTeamId",
                value: null);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000004"),
                column: "ParentTeamId",
                value: null);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"),
                column: "ParentTeamId",
                value: null);

            // Set IsManagement on existing Lead role definitions and rename them
            migrationBuilder.Sql(@"
                UPDATE team_role_definitions
                SET ""IsManagement"" = true, ""Name"" = 'Coordinator'
                WHERE ""Name"" = 'Lead'");

            migrationBuilder.CreateIndex(
                name: "IX_teams_ParentTeamId",
                table: "teams",
                column: "ParentTeamId");

            migrationBuilder.AddForeignKey(
                name: "FK_teams_teams_ParentTeamId",
                table: "teams",
                column: "ParentTeamId",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_teams_teams_ParentTeamId",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "IX_teams_ParentTeamId",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "ParentTeamId",
                table: "teams");

            // Rename Coordinator role definitions back to Lead (must happen before IsManagement column is dropped)
            migrationBuilder.Sql(@"
                UPDATE team_role_definitions
                SET ""Name"" = 'Lead'
                WHERE ""Name"" = 'Coordinator' AND ""IsManagement"" = true");

            migrationBuilder.DropColumn(
                name: "IsManagement",
                table: "team_role_definitions");

            // Reverse string-stored enum renames
            migrationBuilder.Sql("UPDATE team_members SET \"Role\" = 'Lead' WHERE \"Role\" = 'Coordinator'");
            migrationBuilder.Sql("UPDATE teams SET \"SystemTeamType\" = 'Leads' WHERE \"SystemTeamType\" = 'Coordinators'");
            migrationBuilder.Sql("UPDATE contact_fields SET \"Visibility\" = 'LeadsAndBoard' WHERE \"Visibility\" = 'CoordinatorsAndBoard'");
            migrationBuilder.Sql("UPDATE user_emails SET \"Visibility\" = 'LeadsAndBoard' WHERE \"Visibility\" = 'CoordinatorsAndBoard'");

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000002"),
                columns: new[] { "Description", "Name", "Slug", "SystemTeamType" },
                values: new object[] { "All team leads", "Leads", "leads", "Leads" });
        }
    }
}
