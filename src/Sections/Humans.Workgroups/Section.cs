using System.Globalization;
using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.AuditLog.Contracts;
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
using Humans.Workgroups.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Humans.Notifications.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Workgroups;

public sealed class Section :
    ISection,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionChrome,
    ISectionJobs,
    ISectionMemberDashboard,
    ISectionThingsToDo
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
        services.AddSingleton<INotificationSourceContributor, WorkgroupsNotificationSources>();
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

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("workgroups.queue", "Workgroups", "fa-solid fa-people-group", QueueAsync,
            Controller: "WorkgroupsAdmin", Action: "Index",
            Policy: PolicyNames.BoardOrAdmin, Weight: 55)
    ];

    private static async ValueTask<AdminTileValue?> QueueAsync(IServiceProvider sp, CancellationToken ct)
    {
        var register = await sp.GetRequiredService<IWorkgroupService>().GetRegisterAsync(ct);
        var now = sp.GetRequiredService<IClock>().GetCurrentInstant();

        var pending = register.Count(w => w.Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred);
        var awaiting = register.Sum(w => w.AwaitingDisposition().Count());
        var overdue = register.Count(w => w.IsRegistrationOverdue(now)) + register.Count(w => w.HasOverdueDisposition(now));

        // Nothing waiting on a human: the tile disappears rather than reading zero.
        if (pending == 0 && awaiting == 0)
            return null;

        return new AdminTileValue(
            pending.ToString(CultureInfo.CurrentCulture),
            Detail: awaiting == 0 ? null : $"{awaiting} awaiting a disposition",
            Severity: overdue > 0 ? TileSeverity.Warning : TileSeverity.Normal,
            Summary: $"{pending} working groups waiting on the Secretary");
    }

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new ChromeComponent(ChromeSlots.GovernanceDashboard, typeof(GovernanceWorkgroupsViewComponent), Weight: 20)];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            WorkgroupRhythmJob.JobId, typeof(WorkgroupRhythmJob), "0 6 * * *");
    }

    IEnumerable<ChromeComponent> ISectionMemberDashboard.Components() =>
        [new ChromeComponent(ChromeSlots.MemberDashboard, typeof(MyWorkgroupsViewComponent), Weight: 35)];

    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return [];

        var now = services.GetRequiredService<IClock>().GetCurrentInstant();
        var mine = await services.GetRequiredService<IWorkgroupService>().GetForMemberAsync(userId, ct);
        var coordinated = mine.Where(w => w.CoordinatorUserIds().Contains(userId)).ToList();
        if (coordinated.Count == 0)
            return [];

        var localizer = services.GetRequiredService<IStringLocalizer<WorkgroupsResource>>();
        var entries = new List<ThingsToDoEntry>();

        foreach (var workgroup in coordinated.Where(w => w.PendingMembers().Any()))
        {
            entries.Add(new ThingsToDoEntry($"workgroup-requests-{workgroup.Id}",
                localizer["Workgroups_Todo_MembershipRequests_Title"].Value,
                "fa-solid fa-user-plus",
                RawHref: $"/Workgroups/{workgroup.Slug}", Weight: 40)
            {
                Description = string.Format(CultureInfo.CurrentCulture,
                    localizer["Workgroups_Todo_MembershipRequests_Description"].Value,
                    workgroup.Name, workgroup.PendingMembers().Count()),
                ActionText = localizer["Workgroups_Todo_MembershipRequests_Action"].Value,
            });
        }

        foreach (var workgroup in coordinated.Where(w => w.IsUpdateDue(now)))
        {
            entries.Add(new ThingsToDoEntry($"workgroup-update-{workgroup.Id}",
                localizer["Workgroups_Todo_UpdateDue_Title"].Value,
                "fa-solid fa-people-group",
                RawHref: $"/Workgroups/{workgroup.Slug}", Weight: 45)
            {
                Description = string.Format(CultureInfo.CurrentCulture,
                    localizer["Workgroups_Todo_UpdateDue_Description"].Value, workgroup.Name),
                ActionText = localizer["Workgroups_Todo_UpdateDue_Action"].Value,
            });
        }

        return entries;
    }
}
