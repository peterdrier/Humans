using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class HoldedMirrorMovesToHoldedSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_ledger_lines");

            migrationBuilder.DropTable(
                name: "holded_sync_states");

            migrationBuilder.CreateTable(
                name: "holded_doc_sync_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastSyncedDocCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_doc_sync_state", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holded_doc_sync_state");

            migrationBuilder.CreateTable(
                name: "holded_ledger_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountNum = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Date = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    EntryNumber = table.Column<int>(type: "integer", nullable: false),
                    LastSyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Line = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_ledger_lines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "holded_sync_states",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastSyncedDocCount = table.Column<int>(type: "integer", nullable: false),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SyncStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holded_sync_states", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "holded_sync_states",
                columns: new[] { "Id", "LastError", "LastSyncAt", "LastSyncedDocCount", "StatusChangedAt", "SyncStatus" },
                values: new object[] { 1, null, null, 0, null, "Idle" });

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
    }
}
