using Humans.Teams.Domain;
using Humans.Teams.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Teams.Data;

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
