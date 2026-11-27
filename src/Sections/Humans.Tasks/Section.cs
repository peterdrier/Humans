using System.Globalization;
using System.Security.Claims;
using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Demo;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Data;
using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using Humans.Tasks.Services.Demo;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Humans.Tasks.Controllers;

namespace Humans.Tasks;

public sealed class Section : SectionBase, ISectionRoles, ISectionNav, ISectionAdminNav, ISectionPolicies, ISectionPermissions,
    ISectionThingsToDo, ISectionChrome, ITeamTasksPart
{
    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.WidgetGallery, typeof(ViewComponents.TasksGalleryViewComponent))];

    Type ITeamTasksPart.Card => typeof(ViewComponents.TeamTasksCardViewComponent);

    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(TasksSystemRoles.TasksAdmin, "Tasks Admin",
            "Manages the tasks of every team, and reads the tasks admin page. Teams manage their own tasks without it.",
            "fa-solid fa-list-check"),
    ];

    IReadOnlyList<Permission> ISectionPermissions.Permissions { get; } =
    [
        new(TasksPermissions.Manage, "Tasks_Permission_Manage_Title", "Tasks_Permission_Manage_Blurb",
            [TeamOwner.Kind], TopLevelOnly: false, AdminRoles: [TasksSystemRoles.TasksAdmin], typeof(TasksResource)),
    ];

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Nav_Tasks", Controller: "Tasks", Action: "Index", Weight: 22)];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
        [AdminGroup([AdminLink<TasksAdminController>("Tasks", nameof(TasksAdminController.Index), "fa-solid fa-list-check", TasksPolicies.Read)])];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(TasksPolicies.Read, policy =>
            policy.RequireRole(TasksSystemRoles.TasksAdmin, RoleNames.Board, RoleNames.Admin));

    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [TaskItemStatus.Todo] = "bg-light text-dark",
            [TaskItemStatus.InProgress] = "bg-info",
            [TaskItemStatus.Blocked] = "bg-warning text-dark",
            [TaskItemStatus.Done] = "bg-success",
            [TaskItemStatus.Cancelled] = "bg-secondary",
        });

        services.AddSingleton<IAuditActionContributor, TasksAuditActions>();
        services.AddSectionDbContext<TasksDbContext>(sentinelTable: "task_items");

        services.AddSingleton<ITaskRepository, TaskRepository>();   // IDbContextFactory => Singleton-safe

        services.AddDemoStory<TasksDemoStory>(configuration);

        services.AddScoped<TasksEmails>();
        services.AddScoped<IEmailPreviewContributor, TasksEmailPreviews>();

        services.AddScoped<TaskService>();
        services.AddScoped<ITaskService>(sp => sp.GetRequiredService<TaskService>());
        services.AddScoped<ITaskWrite>(sp => sp.GetRequiredService<TaskService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TaskService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TaskService>());
    }

    /// <summary>
    /// One entry per team that has requests waiting, for the people who manage it. <c>Tasks.Manage</c> already reaches
    /// every team for TasksAdmin and Admin, so they see every team's.
    /// </summary>
    async ValueTask<IEnumerable<ThingsToDoEntry>> ISectionThingsToDo.EntriesAsync(
        IServiceProvider services, ClaimsPrincipal user, CancellationToken ct)
    {
        if (user.GetUserId() is not { } userId)
            return [];

        var managed = (await services.GetRequiredService<IPermissionService>()
                .GetOwnersAsync(userId, TasksPermissions.Manage, ct))
            .Where(o => string.Equals(o.Kind, TeamOwner.Kind, StringComparison.Ordinal))
            .Select(o => o.Id)
            .ToHashSet();
        if (managed.Count == 0)
            return [];

        var waiting = (await services.GetRequiredService<ITaskService>().GetTasksAsync(ct))
            .Where(t => managed.Contains(t.Owner.Id))
            .GroupBy(t => t.Owner.Id)
            .Select(g => (TeamId: g.Key, g.First().TeamName, g.First().TeamSlug, Count: g.Sum(t => t.WaitingRequests.Count())))
            .Where(t => t.Count > 0)
            .ToList();
        if (waiting.Count == 0)
            return [];

        var localizer = services.GetRequiredService<IStringLocalizer<TasksResource>>();
        return waiting.Select(t => new ThingsToDoEntry($"tasks-requests-{t.TeamId}",
            localizer["Tasks_Todo_Requests_Title"].Value,
            "fa-solid fa-hand",
            RawHref: $"/Tasks/Team/{(string.IsNullOrEmpty(t.TeamSlug) ? t.TeamId.ToString() : t.TeamSlug)}", Weight: 40)
        {
            Description = localizer["Tasks_Todo_Requests_Description", t.TeamName, t.Count].Value,
            ActionText = localizer["Tasks_Todo_Requests_Action"].Value,
        });
    }
}
