using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations.Auth
{
    /// <inheritdoc />
    public partial class BaselineAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "role_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ValidFrom = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_assignments", x => x.Id);
                    table.CheckConstraint("CK_role_assignments_valid_window", "\"ValidTo\" IS NULL OR \"ValidTo\" > \"ValidFrom\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_RoleName",
                table: "role_assignments",
                column: "RoleName");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_UserId",
                table: "role_assignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_UserId_RoleName",
                table: "role_assignments",
                columns: new[] { "UserId", "RoleName" },
                filter: "\"ValidTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_UserId_RoleName_ValidFrom",
                table: "role_assignments",
                columns: new[] { "UserId", "RoleName", "ValidFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_assignments");
        }
    }
}
