using Humans.Teams.Domain;
using Humans.Teams.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Teams.Data;

/// <summary>
/// Per-section database context for the Teams section
/// (nobodies-collective/Humans#858): maps only <c>teams</c>,
/// <c>team_members</c>, <c>team_join_requests</c>,
/// <c>team_join_request_state_history</c>, <c>team_role_definitions</c>,
/// <c>team_role_assignments</c> and <c>team_early_entry_grants</c>, with its
/// own <c>__EFMigrationsHistory_Teams</c> table and migrations under
/// <c>Migrations/Teams/</c>. Same database, same connection — the split
/// is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like every section context (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Every user reference on these tables (<c>UserId</c>, <c>ReviewedByUserId</c>,
/// <c>ChangedByUserId</c>, <c>AssignedByUserId</c>, <c>CreatedByUserId</c>,
/// <c>PageContentUpdatedByUserId</c>) is a bare Guid, so the Identity tables
/// stay outside this model and are deliberately absent here.
/// </remarks>
internal sealed class TeamsDbContext(DbContextOptions<TeamsDbContext> options)
    : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamJoinRequest> TeamJoinRequests => Set<TeamJoinRequest>();
    public DbSet<TeamJoinRequestStateHistory> TeamJoinRequestStateHistories => Set<TeamJoinRequestStateHistory>();
    public DbSet<TeamRoleDefinition> TeamRoleDefinitions => Set<TeamRoleDefinition>();
    public DbSet<TeamRoleAssignment> TeamRoleAssignments => Set<TeamRoleAssignment>();
    public DbSet<TeamEarlyEntryGrant> TeamEarlyEntryGrants => Set<TeamEarlyEntryGrant>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new TeamConfiguration());
        builder.ApplyConfiguration(new TeamMemberConfiguration());
        builder.ApplyConfiguration(new TeamJoinRequestConfiguration());
        builder.ApplyConfiguration(new TeamJoinRequestStateHistoryConfiguration());
        builder.ApplyConfiguration(new TeamRoleDefinitionConfiguration());
        builder.ApplyConfiguration(new TeamRoleAssignmentConfiguration());
        builder.ApplyConfiguration(new TeamEarlyEntryGrantConfiguration());
    }
}
