using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAdultsKidsEventCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000006"),
                column: "DisplayOrder",
                value: 8);

            migrationBuilder.InsertData(
                table: "event_categories",
                columns: new[] { "Id", "DisplayOrder", "IsActive", "IsSensitive", "Name", "Slug" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0026-000000000007"), 6, true, true, "Adults", "adults" },
                    { new Guid("00000000-0000-0000-0026-000000000008"), 7, true, false, "Kids", "kids" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000007"));

            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000008"));

            migrationBuilder.UpdateData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000006"),
                column: "DisplayOrder",
                value: 6);
        }
    }
}
