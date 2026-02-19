using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixApplicationMembershipTierDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fix: The AddOnboardingRedesignDataModel migration added MembershipTier to applications
            // with defaultValue: "" (empty string). The backfill only updated Approved rows to 'Asociado'.
            // Non-Approved rows (Submitted, UnderReview, Rejected, Withdrawn) still have "" which causes
            // EF Core deserialization failures. All pre-existing applications were for Asociado tier.
            migrationBuilder.Sql("""
                UPDATE applications
                SET "MembershipTier" = 'Asociado'
                WHERE "MembershipTier" = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No reversal — the empty string default was a bug, not intentional state.
        }
    }
}
