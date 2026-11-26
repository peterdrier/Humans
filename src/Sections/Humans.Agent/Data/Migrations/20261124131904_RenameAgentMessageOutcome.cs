using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Agent.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameAgentMessageOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RefusalReason",
                table: "agent_messages",
                newName: "Outcome");

            migrationBuilder.RenameIndex(
                name: "IX_agent_messages_RefusalReason",
                table: "agent_messages",
                newName: "IX_agent_messages_Outcome");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Outcome",
                table: "agent_messages",
                newName: "RefusalReason");

            migrationBuilder.RenameIndex(
                name: "IX_agent_messages_Outcome",
                table: "agent_messages",
                newName: "IX_agent_messages_RefusalReason");
        }
    }
}
