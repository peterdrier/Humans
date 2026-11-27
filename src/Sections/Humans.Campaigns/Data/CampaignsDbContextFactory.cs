using Humans.Base.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Humans.Campaigns.Data;

internal sealed class CampaignsDbContextFactory : SectionDesignTimeDbContextFactory<CampaignsDbContext>
{
    protected override CampaignsDbContext Create(DbContextOptions<CampaignsDbContext> options) => new(options);
}
