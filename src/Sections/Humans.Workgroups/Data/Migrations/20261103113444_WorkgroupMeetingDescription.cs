using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Workgroups.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkgroupMeetingDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "workgroup_meetings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Description",
                table: "workgroup_meetings");
        }
    }
}
