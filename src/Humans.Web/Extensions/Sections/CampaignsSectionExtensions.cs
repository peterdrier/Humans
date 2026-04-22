using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories;
using CampaignsCampaignService = Humans.Application.Services.Campaigns.CampaignService;

namespace Humans.Web.Extensions.Sections;

internal static class CampaignsSectionExtensions
{
    internal static IServiceCollection AddCampaignsSection(this IServiceCollection services)
    {
        // Campaigns section — §15 repository pattern (issue #546).
        // No caching decorator: campaigns are admin-only, infrequent mutations.
        services.AddScoped<ICampaignRepository, CampaignRepository>();
        services.AddScoped<CampaignsCampaignService>();
        services.AddScoped<ICampaignService>(sp => sp.GetRequiredService<CampaignsCampaignService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampaignsCampaignService>());

        return services;
    }
}
