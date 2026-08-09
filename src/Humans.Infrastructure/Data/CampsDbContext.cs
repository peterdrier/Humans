using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Camps;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Camps section
/// (nobodies-collective/Humans#858): maps only <c>camps</c>,
/// <c>camp_seasons</c>, <c>camp_historical_names</c>, <c>camp_images</c>,
/// <c>camp_settings</c>, <c>camp_members</c>, <c>camp_role_definitions</c> and
/// <c>camp_role_assignments</c>, with its own
/// <c>__EFMigrationsHistory_Camps</c> table and migrations under
/// <c>Migrations/Camps/</c>. Same database, same connection — the split is a
/// code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Camp leads and members are bare Guid references to users, so the Identity
/// tables stay in <see cref="HumansDbContext"/> and are deliberately absent
/// here; <c>camp_polygons</c> belongs to City Planning and is likewise absent.
/// <c>CampSettings</c> carries a model-level <c>HasData</c> singleton that the
/// baseline regenerates.
/// </remarks>
internal sealed class CampsDbContext(DbContextOptions<CampsDbContext> options)
    : DbContext(options)
{
    public DbSet<Camp> Camps => Set<Camp>();
    public DbSet<CampSeason> CampSeasons => Set<CampSeason>();
    public DbSet<CampHistoricalName> CampHistoricalNames => Set<CampHistoricalName>();
    public DbSet<CampImage> CampImages => Set<CampImage>();
    public DbSet<CampSettings> CampSettings => Set<CampSettings>();
    public DbSet<CampMember> CampMembers => Set<CampMember>();
    public DbSet<CampRoleDefinition> CampRoleDefinitions => Set<CampRoleDefinition>();
    public DbSet<CampRoleAssignment> CampRoleAssignments => Set<CampRoleAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new CampConfiguration());
        builder.ApplyConfiguration(new CampSeasonConfiguration());
        builder.ApplyConfiguration(new CampHistoricalNameConfiguration());
        builder.ApplyConfiguration(new CampImageConfiguration());
        builder.ApplyConfiguration(new CampSettingsConfiguration());
        builder.ApplyConfiguration(new CampMemberConfiguration());
        builder.ApplyConfiguration(new CampRoleDefinitionConfiguration());
        builder.ApplyConfiguration(new CampRoleAssignmentConfiguration());
    }
}
