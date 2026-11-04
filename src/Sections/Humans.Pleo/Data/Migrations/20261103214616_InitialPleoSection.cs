using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Humans.Pleo.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialPleoSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pleo_accounts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pleo_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pleo_balance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CurrentMinors = table.Column<long>(type: "bigint", nullable: false),
                    AvailableMinors = table.Column<long>(type: "bigint", nullable: false),
                    PendingDebitsMinors = table.Column<long>(type: "bigint", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pleo_balance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pleo_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HoldedCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PleoCategoryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SetByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SetAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pleo_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pleo_employees",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pleo_employees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pleo_entries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EmployeeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TeamId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CategoryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Family = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SubFamily = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExportStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReviewStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PerformedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SettledAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    BookkeepingDate = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Minors = table.Column<long>(type: "bigint", nullable: false),
                    AmountEur = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    MerchantName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    MerchantCategoryCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MerchantCountry = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReceiptCount = table.Column<int>(type: "integer", nullable: false),
                    Raw = table.Column<string>(type: "jsonb", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pleo_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pleo_sync_states",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SyncStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pleo_sync_states", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "pleo_categories",
                columns: new[] { "Id", "HoldedCode", "Name", "PleoCategoryId", "SetAt", "SetByUserId" },
                values: new object[,]
                {
                    { new Guid("06380c3d-eea8-4db0-8177-af4a2fd6fe57"), "62900115", "Dept - NoInfo", null, null, null },
                    { new Guid("0e08a552-946a-4839-a48b-6144fd58d57c"), "62900116", "Dept - Participant Wellness", null, null, null },
                    { new Guid("11d69694-1795-4a99-bd86-b60717f61c87"), "62900113", "Dept - LNT", null, null, null },
                    { new Guid("1703039b-0395-4606-96eb-1d108bf25393"), "62900139", "Dept - Barrio Support", null, null, null },
                    { new Guid("4ecb4d76-813c-4251-99ed-48d14202f3d2"), "62900107", "Dept - City Planning", null, null, null },
                    { new Guid("52754e08-1f8f-4fd7-aa66-9e8e8c55d617"), "62900111", "Dept - Inclusion", null, null, null },
                    { new Guid("6a9ddd36-f240-41fc-9af1-ca03a5d37b46"), "62900110", "Dept - GGP", null, null, null },
                    { new Guid("7f07ac5e-f72e-417c-b299-7e35354736e5"), "62900112", "Dept - Infrastructure", null, null, null },
                    { new Guid("963cecd9-2299-438c-aeb0-15dacba3a10d"), "62900109", "Dept - Creativity", null, null, null },
                    { new Guid("b3094c58-d32c-4f32-ab25-bcfd9737d1b7"), "62900118", "Dept - Production", null, null, null },
                    { new Guid("cde2932c-849a-44c4-8118-2eec7997f862"), "62900141", "WG - ALM 2026", null, null, null },
                    { new Guid("ec1e51be-d96b-453f-8318-c79371282045"), "62900117", "Dept - Power", null, null, null },
                    { new Guid("f2fe14a1-c8b7-43ea-b6eb-dfb73da28541"), "62900114", "Dept - La Cantina", null, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_pleo_categories_PleoCategoryId",
                table: "pleo_categories",
                column: "PleoCategoryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pleo_accounts");

            migrationBuilder.DropTable(
                name: "pleo_balance");

            migrationBuilder.DropTable(
                name: "pleo_categories");

            migrationBuilder.DropTable(
                name: "pleo_employees");

            migrationBuilder.DropTable(
                name: "pleo_entries");

            migrationBuilder.DropTable(
                name: "pleo_sync_states");
        }
    }
}
