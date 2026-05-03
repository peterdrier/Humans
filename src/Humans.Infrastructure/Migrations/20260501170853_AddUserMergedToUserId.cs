using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserMergedToUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "MergedAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MergedToUserId",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_MergedToUserId",
                table: "users",
                column: "MergedToUserId",
                filter: "\"MergedToUserId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_users_users_MergedToUserId",
                table: "users",
                column: "MergedToUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_users_MergedToUserId",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_MergedToUserId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MergedToUserId",
                table: "users");
        }
    }
}
