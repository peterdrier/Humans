using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Email.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services;
using Humans.Base.Hosting;
using Humans.Base.Models.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;
using Humans.AuditLog.Contracts;

namespace Humans.Campaigns;

/// <summary>No caching decorator: the admin pages are the only readers and they are not hot.</summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, CampaignsAuditActions>();
        services.AddSectionDbContext<CampaignsDbContext>(sentinelTable: "campaigns");

        services.AddSingleton<ICampaignRepository, CampaignRepository>();
        services.AddScoped<CampaignsEmails>();
        services.AddScoped<IEmailPreviewContributor, CampaignsEmailPreviews>();

        services.AddScoped<CampaignService>();
        services.AddScoped<ICampaignManagement>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<ICampaignService>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<ICampaignWrite>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CampaignService>());

        // Base cannot name an internal CampaignStatus, so the section registers its badge colours.
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [CampaignStatus.Draft] = "bg-secondary",
            [CampaignStatus.Active] = "bg-success",
            [CampaignStatus.Completed] = "bg-info",
        });
    }
}
