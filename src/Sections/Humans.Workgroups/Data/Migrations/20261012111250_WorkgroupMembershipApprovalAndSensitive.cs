using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Workgroups.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkgroupMembershipApprovalAndSensitive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSensitive",
                table: "workgroups",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPending",
                table: "workgroup_members",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSensitive",
                table: "workgroups");

            migrationBuilder.DropColumn(
                name: "IsPending",
                table: "workgroup_members");
        }
    }
}
