using Humans.Domain.Entities;
using Humans.Infrastructure.Data.Configurations.Campaigns;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Per-section database context for the Campaigns section
/// (nobodies-collective/Humans#858): maps only <c>campaigns</c>,
/// <c>campaign_codes</c> and <c>campaign_grants</c>, with its own
/// <c>__EFMigrationsHistory_Campaigns</c> table and migrations under
/// <c>Migrations/Campaigns/</c>. Same database, same connection — the split is
/// a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// Internal-sealed like <see cref="HumansDbContext"/> (issue #750): repositories
/// are the only consumers. Configurations are applied explicitly (not by
/// assembly scanning) so this model can never accrete another section's tables.
/// Grant recipients and campaign creators are bare Guid references, so the
/// Identity tables stay in <see cref="HumansDbContext"/> and are deliberately
/// absent here.
/// </remarks>
internal sealed class CampaignsDbContext(DbContextOptions<CampaignsDbContext> options)
    : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignCode> CampaignCodes => Set<CampaignCode>();
    public DbSet<CampaignGrant> CampaignGrants => Set<CampaignGrant>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new CampaignConfiguration());
        builder.ApplyConfiguration(new CampaignCodeConfiguration());
        builder.ApplyConfiguration(new CampaignGrantConfiguration());
    }
}
