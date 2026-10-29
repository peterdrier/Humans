using Humans.Base.Hosting;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models.Tables;
using Humans.Calendar.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Authorization;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Jobs;
using Humans.Workgroups.Services;
using Humans.Workgroups.Services.Contributors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Workgroups;

public sealed class Section : ISection, ISectionAdminNav
{
    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Workgroups", [
            new("Workgroups", "WorkgroupsAdmin", "Index", null, null,
                "fa-solid fa-people-group", PolicyNames.BoardOrAdmin)
        ])
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, WorkgroupsAuditActions>();
        services.AddSectionDbContext<WorkgroupsDbContext>(sentinelTable: "workgroups");

        services.AddSingleton<IWorkgroupRepository, WorkgroupRepository>();

        services.AddKeyedScoped<IWorkgroupService, WorkgroupService>(CachingWorkgroupService.InnerServiceKey);
        services.AddSingleton<CachingWorkgroupService>();
        services.AddSingleton<IWorkgroupService>(sp => sp.GetRequiredService<CachingWorkgroupService>());

        // The GDPR and merge fan-outs bind to the decorator: erasure and the merge fold change cached rows.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingWorkgroupService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CachingWorkgroupService>());

        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingWorkgroupService>().RegisterCacheStats);

        services.AddScoped<ICalendarFeedContributor, WorkgroupCalendarContributor>();
        services.AddScoped<IGoogleDriveAccessSource, WorkgroupDriveAccessSource>();

        services.AddScoped<IAuthorizationHandler, WorkgroupAuthorizationHandler>();

        services.AddScoped<WorkgroupRhythmJob>();

        services.AddScoped<WorkgroupsEmails>();
        services.AddScoped<IEmailPreviewContributor, WorkgroupsEmailPreviews>();

        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [WorkgroupStatus.Applied] = "bg-warning text-dark",
            [WorkgroupStatus.Referred] = "bg-info text-dark",
            [WorkgroupStatus.Active] = "bg-success",
            [WorkgroupStatus.Refused] = "bg-secondary",
            [WorkgroupStatus.Withdrawn] = "bg-dark",
            [WorkgroupStatus.Dormant] = "bg-secondary",
            [WorkgroupDocumentStatus.Draft] = "bg-secondary",
            [WorkgroupDocumentStatus.Published] = "bg-primary",
            [WorkgroupDocumentStatus.Delivered] = "bg-success",
            [WorkgroupDisposition.Accepted] = "bg-success",
            [WorkgroupDisposition.Declined] = "bg-danger",
            [WorkgroupDisposition.Deferred] = "bg-warning text-dark",
            [WorkgroupDisposition.Noted] = "bg-secondary",
            [WorkgroupCommentDisposition.Pending] = "bg-warning text-dark",
            [WorkgroupCommentDisposition.Accepted] = "bg-success",
            [WorkgroupCommentDisposition.Rejected] = "bg-secondary",
            [WorkgroupCommentDisposition.Incorporated] = "bg-success",
            [WorkgroupCommentDisposition.Noted] = "bg-info text-dark",
        });
    }
}
