using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Models.Tables;
using Humans.Calendar.Contracts;
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

namespace Humans.Workgroups;

/// <summary>
/// Workgroups' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<WorkgroupsDbContext>(sentinelTable: "workgroups");

        // Singleton + IDbContextFactory pattern (§15b): the repository owns context lifetime.
        services.AddSingleton<IWorkgroupRepository, WorkgroupRepository>();

        // Inner service — Scoped + keyed; the Singleton decorator resolves it per call and is
        // what an unkeyed IWorkgroupService resolves to.
        services.AddKeyedScoped<IWorkgroupService, WorkgroupService>(CachingWorkgroupService.InnerServiceKey);
        services.AddSingleton<CachingWorkgroupService>();
        services.AddSingleton<IWorkgroupService>(sp => sp.GetRequiredService<CachingWorkgroupService>());

        // GDPR and account-merge fan-outs bind to the decorator, not the inner: erasure and the
        // merge fold change cached rows.
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CachingWorkgroupService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<CachingWorkgroupService>());

        // Surface the register cache on /Debug/CacheStats.
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingWorkgroupService>().RegisterCacheStats);

        // Inbound fan-outs: Calendar and GoogleIntegration name nothing of ours.
        services.AddScoped<ICalendarFeedContributor, WorkgroupCalendarContributor>();
        services.AddScoped<IGoogleDriveAccessSource, WorkgroupDriveAccessSource>();

        services.AddScoped<IAuthorizationHandler, WorkgroupAuthorizationHandler>();

        services.AddScoped<WorkgroupRhythmJob>();

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
