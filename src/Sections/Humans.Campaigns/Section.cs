using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services;
using Humans.Base.Hosting;
using Humans.Base.Models.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Campaigns;

/// <summary>
/// Campaigns' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix. Plain Scoped service — no caching
/// decorator; the admin pages are the only readers and they are not hot.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<CampaignsDbContext>(sentinelTable: "campaigns");

        // §15 repository pattern (nobodies-collective/Humans#546): Singleton +
        // IDbContextFactory (§15b) so the repository owns context lifetime.
        services.AddSingleton<ICampaignRepository, CampaignRepository>();
        services.AddScoped<CampaignService>();
        services.AddScoped<ICampaignService>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<ICampaignServiceRead>(sp => sp.GetRequiredService<CampaignService>());
        // Owns the user-scoped campaign_grants table → GDPR export contributor and
        // account-merge fold participant (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CampaignService>());

        // Section-owned badge colours: Base cannot name an internal CampaignStatus
        // (memory/architecture/base-ui-registries-are-section-populated.md).
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [CampaignStatus.Draft] = "bg-secondary",
            [CampaignStatus.Active] = "bg-success",
            [CampaignStatus.Completed] = "bg-info",
        });
    }
}
