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
                SET ""Email"" = REGEXP_REPLACE(""Email"", '@googlemail\.com$', '@gmail.com', 'i'),
                    ""NormalizedEmail"" = UPPER(REGEXP_REPLACE(""Email"", '@googlemail\.com$', '@gmail.com', 'i')),
                    ""UserName"" = REGEXP_REPLACE(""UserName"", '@googlemail\.com$', '@gmail.com', 'i'),
                    ""NormalizedUserName"" = UPPER(REGEXP_REPLACE(""UserName"", '@googlemail\.com$', '@gmail.com', 'i'))
                WHERE ""Email"" ILIKE '%@googlemail.com';

                UPDATE user_emails
                SET ""Email"" = REGEXP_REPLACE(""Email"", '@googlemail\.com$', '@gmail.com', 'i')
                WHERE ""Email"" ILIKE '%@googlemail.com';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
