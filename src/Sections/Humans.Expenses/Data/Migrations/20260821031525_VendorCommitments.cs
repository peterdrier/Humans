using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Expenses.Data.Migrations
{
    /// <inheritdoc />
    public partial class VendorCommitments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vendor_commitments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExpectedAmount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BudgetCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QuoteFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    QuoteContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    QuoteExtension = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    QuoteUploadedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    MatchedHoldedDocId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MatchedHoldedDocNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MatchedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendor_commitments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "vendor_commitment_match_candidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorCommitmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldedDocId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HoldedDocNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContactName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocDate = table.Column<LocalDate>(type: "date", nullable: false),
                    DocTotal = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DetectedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Accepted = table.Column<bool>(type: "boolean", nullable: true),
                    ResolvedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendor_commitment_match_candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_vendor_commitment_match_candidates_vendor_commitments_Vendo~",
                        column: x => x.VendorCommitmentId,
                        principalTable: "vendor_commitments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vendor_commitment_payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorCommitmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    PaidOn = table.Column<LocalDate>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vendor_commitment_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_vendor_commitment_payments_vendor_commitments_VendorCommitm~",
                        column: x => x.VendorCommitmentId,
                        principalTable: "vendor_commitments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commitment_match_candidates_ResolvedAt",
                table: "vendor_commitment_match_candidates",
                column: "ResolvedAt");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commitment_match_candidates_VendorCommitmentId_Holde~",
                table: "vendor_commitment_match_candidates",
                columns: new[] { "VendorCommitmentId", "HoldedDocId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commitment_payments_VendorCommitmentId",
                table: "vendor_commitment_payments",
                column: "VendorCommitmentId");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commitments_BudgetCategoryId",
                table: "vendor_commitments",
                column: "BudgetCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commitments_MatchedHoldedDocId",
                table: "vendor_commitments",
                column: "MatchedHoldedDocId");

            migrationBuilder.CreateIndex(
                name: "IX_vendor_commitments_Status",
                table: "vendor_commitments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vendor_commitment_match_candidates");

            migrationBuilder.DropTable(
                name: "vendor_commitment_payments");

            migrationBuilder.DropTable(
                name: "vendor_commitments");
        }
    }
}
