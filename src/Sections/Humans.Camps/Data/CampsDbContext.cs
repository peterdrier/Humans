using Humans.Camps.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Humans.Camps.Data;

internal sealed class CampsDbContext(DbContextOptions<CampsDbContext> options)
    : DbContext(options)
{
    public DbSet<Camp> Camps => Set<Camp>();
    public DbSet<CampSeason> CampSeasons => Set<CampSeason>();
    public DbSet<CampHistoricalName> CampHistoricalNames => Set<CampHistoricalName>();
    public DbSet<CampImage> CampImages => Set<CampImage>();
    public DbSet<CampSettings> CampSettings => Set<CampSettings>();
    public DbSet<CampEventSettings> CampEventSettings => Set<CampEventSettings>();
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
        builder.ApplyConfiguration(new CampEventSettingsConfiguration());
        builder.ApplyConfiguration(new CampMemberConfiguration());
        builder.ApplyConfiguration(new CampRoleDefinitionConfiguration());
        builder.ApplyConfiguration(new CampRoleAssignmentConfiguration());
    }
}
