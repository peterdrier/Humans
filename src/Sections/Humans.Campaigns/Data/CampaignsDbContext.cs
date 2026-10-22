using Humans.Campaigns.Data.Configurations;
using Humans.Campaigns.Domain;
using Microsoft.EntityFrameworkCore;

namespace Humans.Campaigns.Data;

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
