using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamPublicPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallsToAction",
                table: "teams",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublicPage",
                table: "teams",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PageContent",
                table: "teams",
                type: "character varying(50000)",
                maxLength: 50000,
                nullable: true);

            migrationBuilder.AddColumn<Instant>(
                name: "PageContentUpdatedAt",
                table: "teams",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PageContentUpdatedByUserId",
                table: "teams",
                type: "uuid",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000001"),
                columns: new[] { "CallsToAction", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId" },
                values: new object[] { null, null, null, null });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000002"),
                columns: new[] { "CallsToAction", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId" },
                values: new object[] { null, null, null, null });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000003"),
                columns: new[] { "CallsToAction", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId" },
                values: new object[] { null, null, null, null });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000004"),
                columns: new[] { "CallsToAction", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId" },
                values: new object[] { null, null, null, null });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000005"),
                columns: new[] { "CallsToAction", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId" },
                values: new object[] { null, null, null, null });

            migrationBuilder.UpdateData(
                table: "teams",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0001-000000000006"),
                columns: new[] { "CallsToAction", "PageContent", "PageContentUpdatedAt", "PageContentUpdatedByUserId" },
                values: new object[] { null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CallsToAction",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "IsPublicPage",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "PageContent",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "PageContentUpdatedAt",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "PageContentUpdatedByUserId",
                table: "teams");
        }
    }
}
