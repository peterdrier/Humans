using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Profiles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_UserId_RoleName",
                table: "role_assignments",
                columns: new[] { "UserId", "RoleName" },
                filter: "\"ValidTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_consent_records_UserId_ExplicitConsent_ConsentedAt",
                table: "consent_records",
                columns: new[] { "UserId", "ExplicitConsent", "ConsentedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_applications_UserId_Status",
                table: "applications",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_role_assignments_UserId_RoleName",
                table: "role_assignments");

            migrationBuilder.DropIndex(
                name: "IX_consent_records_UserId_ExplicitConsent_ConsentedAt",
                table: "consent_records");

            migrationBuilder.DropIndex(
                name: "IX_applications_UserId_Status",
                table: "applications");
        }
    }
}
