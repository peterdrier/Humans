using Humans.Application.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services;
using Humans.Infrastructure.Hosting;
using Humans.UI.Models.Tables;
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

        // §15 repository pattern (issue #546): Singleton + IDbContextFactory (§15b) so the
        // repository owns context lifetime.
        services.AddSingleton<ICampaignRepository, CampaignRepository>();
        services.AddScoped<CampaignService>();
        services.AddScoped<ICampaignService>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<ICampaignServiceRead>(sp => sp.GetRequiredService<CampaignService>());
        // Owns the user-scoped campaign_grants table → GDPR export contributor and
        // account-merge fold participant (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CampaignService>());

        // The section owns its badge colours rather than Humans.UI holding a literal row per
        // section enum: Base cannot name CampaignStatus once the enum moves in here, and
        // referencing the section from Base to get it back is the trap that ends with Base
        // knowing every section's vocabulary (Peter, 2026-08-09 —
        // memory/architecture/base-ui-registries-are-section-populated.md).
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [CampaignStatus.Draft] = "bg-secondary",
            [CampaignStatus.Active] = "bg-success",
            [CampaignStatus.Completed] = "bg-info",
        });
    }
}
