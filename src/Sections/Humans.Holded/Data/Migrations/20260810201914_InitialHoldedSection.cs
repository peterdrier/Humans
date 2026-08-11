using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Holded.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialHoldedSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holded_accounts",
                columns: table => new
                {
                    Number = table.Column<int>(type: "integer", nullable: false),
                    HoldedId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Group = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Debit = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_accounts", x => x.Number);
                });

            migrationBuilder.CreateTable(
                name: "holded_api_calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CalledAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    RateLimitRemaining = table.Column<int>(type: "integer", nullable: true),
                    RateLimitWindow = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_api_calls", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "holded_ledger_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryNumber = table.Column<int>(type: "integer", nullable: false),
                    Line = table.Column<int>(type: "integer", nullable: false),
                    AccountNum = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Debit = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_ledger_lines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "holded_sync_states",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SyncStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_sync_states", x => x.Kind);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holded_api_calls_CalledAt",
                table: "holded_api_calls",
                column: "CalledAt");

            migrationBuilder.CreateIndex(
                name: "IX_holded_ledger_lines_AccountNum",
                table: "holded_ledger_lines",
                column: "AccountNum");

            migrationBuilder.CreateIndex(
                name: "IX_holded_ledger_lines_EntryNumber_Line",
                table: "holded_ledger_lines",
                columns: new[] { "EntryNumber", "Line" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_accounts");

            migrationBuilder.DropTable(
                name: "holded_api_calls");

            migrationBuilder.DropTable(
                name: "holded_ledger_lines");

            migrationBuilder.DropTable(
                name: "holded_sync_states");
        }
    }
}
