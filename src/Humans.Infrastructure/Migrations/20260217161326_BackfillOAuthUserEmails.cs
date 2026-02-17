using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillOAuthUserEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill OAuth UserEmail records for existing users who don't have one.
            // Uses the user's Email as the OAuth email with BoardOnly visibility.
            migrationBuilder.Sql("""
                INSERT INTO user_emails ("Id", "UserId", "Email", "IsOAuth", "IsVerified", "IsNotificationTarget", "Visibility", "DisplayOrder", "CreatedAt", "UpdatedAt")
                SELECT
                    gen_random_uuid(),
                    u."Id",
                    u."Email",
                    true,
                    true,
                    true,
                    'BoardOnly',
                    0,
                    now(),
                    now()
                FROM users u
                WHERE u."Email" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM user_emails ue WHERE ue."UserId" = u."Id"
                  )
                """);

            // Fix any rows inserted with integer '0' instead of string 'BoardOnly'
            // (from an earlier version of this migration that used the enum integer value).
            migrationBuilder.Sql("""
                UPDATE user_emails SET "Visibility" = 'BoardOnly' WHERE "Visibility" = '0'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove backfilled OAuth email records (those created by this migration).
            // Only removes records where no other emails exist for the user,
            // preserving any manually added emails.
            migrationBuilder.Sql("""
                DELETE FROM user_emails ue
                WHERE ue."IsOAuth" = true
                  AND (SELECT count(*) FROM user_emails ue2 WHERE ue2."UserId" = ue."UserId") = 1
                """);
        }
    }
}
