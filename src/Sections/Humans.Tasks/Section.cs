using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Base.Demo;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models.Tables;
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

namespace Humans.Tasks;

public sealed class Section : ISection, ISectionRoles, ISectionNav, ISectionAdminNav, ISectionPolicies, ISectionPermissions
{
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
        [new("Tasks", [new("Tasks", "TasksAdmin", "Index", null, null, "fa-solid fa-list-check", TasksPolicies.Read)])];

    void ISectionPolicies.AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(TasksPolicies.Read, policy =>
            policy.RequireRole(TasksSystemRoles.TasksAdmin, RoleNames.Board, RoleNames.Admin));

    public void Register(IServiceCollection services, IConfiguration configuration)
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

        services.AddScoped<TaskService>();
        services.AddScoped<ITaskService>(sp => sp.GetRequiredService<TaskService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<TaskService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<TaskService>());
    }
}
