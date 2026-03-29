using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBarrioLeadsSystemTeam : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "teams",
                columns: new[] { "Id", "CreatedAt", "Description", "GoogleGroupPrefix", "IsActive", "Name", "ParentTeamId", "Slug", "SystemTeamType", "UpdatedAt" },
                values: new object[] { new Guid("00000000-0000-0000-0001-000000000006"), NodaTime.Instant.FromUnixTimeTicks(17702491570000000L), "All active camp leads across all camps", null, true, "Barrio Leads", null, "barrio-leads", "BarrioLeads", NodaTime.Instant.FromUnixTimeTicks(17702491570000000L) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000006"));
        }
    }
}
