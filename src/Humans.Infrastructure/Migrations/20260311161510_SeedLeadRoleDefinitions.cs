using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedLeadRoleDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seed Lead role definitions with SlotCount matching existing lead count (minimum 1)
            migrationBuilder.Sql(@"
                INSERT INTO team_role_definitions (""Id"", ""TeamId"", ""Name"", ""Description"", ""SlotCount"", ""Priorities"", ""SortOrder"", ""CreatedAt"", ""UpdatedAt"")
                SELECT gen_random_uuid(), t.""Id"", 'Lead', 'Team leadership role',
                       GREATEST(1, lead_counts.cnt),
                       rtrim(repeat('Critical,', GREATEST(1, lead_counts.cnt)::int), ','),
                       0, NOW() AT TIME ZONE 'UTC', NOW() AT TIME ZONE 'UTC'
                FROM teams t
                CROSS JOIN LATERAL (
                    SELECT COUNT(*)::int AS cnt
                    FROM team_members tm
                    WHERE tm.""TeamId"" = t.""Id"" AND tm.""Role"" = 1 AND tm.""LeftAt"" IS NULL
                ) lead_counts
                WHERE t.""SystemTeamType"" = 'None'
                AND NOT EXISTS (
                    SELECT 1 FROM team_role_definitions d WHERE d.""TeamId"" = t.""Id"" AND lower(d.""Name"") = 'lead'
                )
            ");

            // Seed slot assignments for existing active Lead members with sequential slot indexes
            migrationBuilder.Sql(@"
                INSERT INTO team_role_assignments (""Id"", ""TeamRoleDefinitionId"", ""TeamMemberId"", ""SlotIndex"", ""AssignedAt"", ""AssignedByUserId"")
                SELECT gen_random_uuid(), d.""Id"", tm.""Id"",
                       (ROW_NUMBER() OVER (PARTITION BY d.""Id"" ORDER BY tm.""JoinedAt"")) - 1,
                       NOW() AT TIME ZONE 'UTC', tm.""UserId""
                FROM team_members tm
                INNER JOIN team_role_definitions d ON d.""TeamId"" = tm.""TeamId"" AND lower(d.""Name"") = 'lead'
                WHERE tm.""Role"" = 1
                AND tm.""LeftAt"" IS NULL
                AND NOT EXISTS (
                    SELECT 1 FROM team_role_assignments a WHERE a.""TeamRoleDefinitionId"" = d.""Id"" AND a.""TeamMemberId"" = tm.""Id""
                )
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
