using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Holded.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldedSyncStateScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_sync_states",
                table: "holded_sync_states");

            migrationBuilder.AddColumn<string>(
                name: "Scope",
                table: "holded_sync_states",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_sync_states",
                table: "holded_sync_states",
                columns: new[] { "Kind", "Scope" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_sync_states",
                table: "holded_sync_states");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "holded_sync_states");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_sync_states",
                table: "holded_sync_states",
                column: "Kind");
        }
    }
}
