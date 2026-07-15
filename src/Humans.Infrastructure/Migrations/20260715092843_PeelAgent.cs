using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Agent peel
    /// (nobodies-collective/Humans#858): <c>agent_conversations</c>,
    /// <c>agent_messages</c> and <c>agent_settings</c> moved to
    /// <c>AgentDbContext</c>, which owns the physical tables from here on. The
    /// scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately
    /// emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
