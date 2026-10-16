using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Preapprovals.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPreapprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pre_programs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PerPersonAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    TotalCeiling = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    HoldedAccountNumber = table.Column<int>(type: "integer", nullable: false),
                    HoldedAccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AcceptsApplications = table.Column<bool>(type: "boolean", nullable: false),
                    ClaimBy = table.Column<LocalDate>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_programs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pre_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    BeneficiaryUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MaxAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ApplicationNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequestedAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ExpenseReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pre_grants_pre_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "pre_programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pre_program_managers",
                columns: table => new
                {
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_program_managers", x => new { x.ProgramId, x.UserId });
                    table.ForeignKey(
                        name: "FK_pre_program_managers_pre_programs_ProgramId",
                        column: x => x.ProgramId,
                        principalTable: "pre_programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pre_reminders_sent",
                columns: table => new
                {
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ClaimBy = table.Column<LocalDate>(type: "date", nullable: false),
                    SentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pre_reminders_sent", x => new { x.GrantId, x.Kind, x.ClaimBy });
                    table.ForeignKey(
                        name: "FK_pre_reminders_sent_pre_grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "pre_grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pre_grants_BeneficiaryUserId",
                table: "pre_grants",
                column: "BeneficiaryUserId");

            migrationBuilder.CreateIndex(
                name: "IX_pre_grants_ExpenseReportId",
                table: "pre_grants",
                column: "ExpenseReportId");

            migrationBuilder.CreateIndex(
                name: "IX_pre_grants_ProgramId_Status",
                table: "pre_grants",
                columns: new[] { "ProgramId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_pre_program_managers_UserId",
                table: "pre_program_managers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_pre_programs_Status",
                table: "pre_programs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pre_program_managers");

            migrationBuilder.DropTable(
                name: "pre_reminders_sent");

            migrationBuilder.DropTable(
                name: "pre_grants");

            migrationBuilder.DropTable(
                name: "pre_programs");
        }
    }
}
