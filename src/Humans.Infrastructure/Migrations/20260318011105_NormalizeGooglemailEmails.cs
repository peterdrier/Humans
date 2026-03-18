using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeGooglemailEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data-only migration: canonicalize @googlemail.com → @gmail.com
            // Google treats these as the same account but string comparison doesn't,
            // causing false drift positives in sync. See #139.
            // REPLACE is case-sensitive, so use REGEXP_REPLACE with 'i' flag
            // to handle any casing of @googlemail.com → @gmail.com
            migrationBuilder.Sql(@"
                UPDATE users
                SET email = REGEXP_REPLACE(email, '@googlemail\.com$', '@gmail.com', 'i'),
                    normalized_email = UPPER(REGEXP_REPLACE(email, '@googlemail\.com$', '@gmail.com', 'i')),
                    user_name = REGEXP_REPLACE(user_name, '@googlemail\.com$', '@gmail.com', 'i'),
                    normalized_user_name = UPPER(REGEXP_REPLACE(user_name, '@googlemail\.com$', '@gmail.com', 'i'))
                WHERE email ILIKE '%@googlemail.com';

                UPDATE user_emails
                SET email = REGEXP_REPLACE(email, '@googlemail\.com$', '@gmail.com', 'i')
                WHERE email ILIKE '%@googlemail.com';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
