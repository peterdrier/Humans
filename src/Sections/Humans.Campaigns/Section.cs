using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models.Tables;
using Humans.Campaigns.Contracts;
using Humans.Campaigns.Data;
using Humans.Campaigns.Domain;
using Humans.Campaigns.Services;
using Humans.Campaigns.ViewComponents;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Campaigns;

/// <summary>No caching decorator: the admin pages are the only readers and they are not hot.</summary>
public sealed class Section : ISection, ISectionAdminNav, IUserPart
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<INotificationSourceContributor, CampaignsNotificationSources>();
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

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Campaigns", [
            new("Campaigns", "Campaign", "Index", null, null, "fa-solid fa-bullhorn", PolicyNames.AdminOnly)
        ])
    ];

    IEnumerable<UserPart> IUserPart.Parts() =>
    [
        new(UserPartSlots.Profile, typeof(MyCodesViewComponent), Weight: 100),
        new(UserPartSlots.AdminDetail, typeof(CampaignCodesViewComponent), Weight: 100),
    ];
}
