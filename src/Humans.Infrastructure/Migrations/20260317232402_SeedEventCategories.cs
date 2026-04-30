using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedEventCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "event_categories",
                columns: new[] { "Id", "DisplayOrder", "IsActive", "IsSensitive", "Name", "Slug" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0026-000000000001"), 1, true, false, "Workshop", "workshop" },
                    { new Guid("00000000-0000-0000-0026-000000000002"), 2, true, false, "Party", "party" },
                    { new Guid("00000000-0000-0000-0026-000000000003"), 3, true, false, "Food and drink", "food-and-drink" },
                    { new Guid("00000000-0000-0000-0026-000000000004"), 4, true, false, "Chillout", "chillout" },
                    { new Guid("00000000-0000-0000-0026-000000000005"), 5, true, true, "Spiritual / Healing", "spiritual-healing" },
                    { new Guid("00000000-0000-0000-0026-000000000006"), 6, true, false, "Other", "other" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000001"));

            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000002"));

            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000003"));

            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000004"));

            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000005"));

            migrationBuilder.DeleteData(
                table: "event_categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0026-000000000006"));
        }
    }
}
