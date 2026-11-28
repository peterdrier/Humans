using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Teams.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeleteGovernanceBodyTeams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000003"));

            migrationBuilder.DeleteData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000004"));

            migrationBuilder.DeleteData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "CallsToAction", "CreatedAt", "CustomSlug", "Description", "EarlyEntryEnabled", "GoogleGroupPrefix", "HasBudget", "IsActive", "IsHidden", "IsPromotedToDirectory", "IsPublicPage", "IsSensitive", "Name", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId", "ParentTeamId", "ShowCoordinatorsOnPublicPage", "Slug", "SystemTeamType", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0001-000000000003"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "Board members with active role assignments", false, null, false, true, false, false, false, false, "Board", null, null, null, null, true, "board", "Board", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000004"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "Voting members with approved asociado applications", false, null, false, true, false, false, false, false, "Asociados", null, null, null, null, true, "asociados", "Asociados", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) },
                    { new Guid("00000000-0000-0000-0001-000000000005"), null, NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), null, "Active contributors with approved colaborador applications", false, null, false, true, false, false, false, false, "Colaboradors", null, null, null, null, true, "colaboradors", "Colaboradors", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) }
                });
        }
    }
}
