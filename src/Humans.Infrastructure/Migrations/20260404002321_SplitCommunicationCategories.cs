using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SplitCommunicationCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Clone EventOperations rows as TeamUpdates (same OptedOut + InboxEnabled)
            migrationBuilder.Sql(@"
                INSERT INTO communication_preferences (""Id"", ""UserId"", ""Category"", ""OptedOut"", ""InboxEnabled"", ""UpdatedAt"", ""UpdateSource"")
                SELECT gen_random_uuid(), ""UserId"", 'TeamUpdates', ""OptedOut"", ""InboxEnabled"", ""UpdatedAt"", 'DataMigration'
                FROM communication_preferences
                WHERE ""Category"" = 'EventOperations'
                ON CONFLICT (""UserId"", ""Category"") DO NOTHING;
            ");

            // 2. Rename EventOperations → VolunteerUpdates
            migrationBuilder.Sql(@"
                UPDATE communication_preferences
                SET ""Category"" = 'VolunteerUpdates', ""UpdateSource"" = 'DataMigration'
                WHERE ""Category"" = 'EventOperations';
            ");

            // 3. Rename CommunityUpdates → FacilitatedMessages
            migrationBuilder.Sql(@"
                UPDATE communication_preferences
                SET ""Category"" = 'FacilitatedMessages', ""UpdateSource"" = 'DataMigration'
                WHERE ""Category"" = 'CommunityUpdates';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: FacilitatedMessages → CommunityUpdates
            migrationBuilder.Sql(@"
                UPDATE communication_preferences
                SET ""Category"" = 'CommunityUpdates', ""UpdateSource"" = 'DataMigration'
                WHERE ""Category"" = 'FacilitatedMessages';
            ");

            // Reverse: VolunteerUpdates → EventOperations
            migrationBuilder.Sql(@"
                UPDATE communication_preferences
                SET ""Category"" = 'EventOperations', ""UpdateSource"" = 'DataMigration'
                WHERE ""Category"" = 'VolunteerUpdates';
            ");

            // Remove TeamUpdates rows created by migration
            migrationBuilder.Sql(@"
                DELETE FROM communication_preferences
                WHERE ""Category"" = 'TeamUpdates' AND ""UpdateSource"" = 'DataMigration';
            ");
        }
    }
}
