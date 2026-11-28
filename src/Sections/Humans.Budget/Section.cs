using Humans.AuditLog.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Models.Tables;
using Humans.Base.Extensions;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Budget.Authorization;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Jobs;
using Humans.Budget.Services;
using Humans.Finance.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Issues.Contracts;
using Humans.Permissions.Contracts;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Budget;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionAdminNav,
    ISectionDataMigrations,
    ISectionJobs,
    ISectionNav,
    ISectionPermissions
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [BudgetYearStatus.Draft] = "bg-warning text-dark",
            [BudgetYearStatus.Active] = "bg-success",
            [BudgetYearStatus.Closed] = "bg-secondary",
        });

        services.AddSingleton<IAuditActionContributor, BudgetAuditActions>();
        services.AddSectionDbContext<BudgetDbContext>(sentinelTable: "budget_years");

        services.AddSingleton<IBudgetRepository, BudgetRepository>();
        services.AddScoped<BudgetService>();
        services.AddScoped<IBudgetManagement>(sp => sp.GetRequiredService<BudgetService>());
        services.AddScoped<IBudgetService>(sp => sp.GetRequiredService<BudgetService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BudgetService>());

        services.AddScoped<TicketingBudgetService>();
        services.AddScoped<ITicketingBudgetService>(sp => sp.GetRequiredService<TicketingBudgetService>());
        services.AddScoped<TicketingBudgetSyncJob>();

        // Development drives this through the Contracts/ interface, which keeps Budget's write methods off the
        // public surface.
        services.AddScoped<IBudgetDemoSeeder, DevelopmentBudgetSeeder>();

        services.AddScoped<IAuthorizationHandler, BudgetAuthorizationHandler>();
    }

    string IIssueQueueOwner.QueueKey => "Budget";

    string IIssueQueueOwner.AreaLabelResourceKey => "Issues_Area_Budget";

    IReadOnlyList<string> IIssueQueueOwner.PathSegments => ["finance", "budget"];

    IReadOnlyList<string> IIssueQueueOwner.OwningRoles => [FinanceRoles.FinanceAdmin];

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Budget", [
            // "BudgetAdmin", not "Finance": the tag helper resolves controller names, not routes, and a name that
            // resolves to no action renders the anchor with no href.
            new("Overview", "BudgetAdmin", "Index", null, null, "fa-solid fa-coins", FinancePolicies.Read)
        ])
    ];

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new("budget.audit-log-to-audit", "Budget",
            "Copy the budget change log into the audit log's temporary side",
            "The budget_audit_logs table, in its drop PR",
            AuditCopyStatusAsync, ApplyAuditCopyAsync),
    ];

    private static async Task<DataMigrationStatus> AuditCopyStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["When", "Entity", "Description"],
            [.. (await sp.GetRequiredService<BudgetService>().GetPendingAuditCopiesAsync(ct))
                .Select(r => (IReadOnlyList<string>)[r.OccurredAt.ToIso8601(), r.EntityType, r.Description])]);

    private static Task<int> ApplyAuditCopyAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<BudgetService>().CopyAuditLogToAuditAsync(actorUserId, ct);

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "budget-ticketing-sync", typeof(TicketingBudgetSyncJob), "30 4 * * *");
    }

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Nav_Budget", Controller: "Budget", Action: "Summary", Policy: PolicyNames.AppAccess, Weight: 70)];

    IReadOnlyList<Permission> ISectionPermissions.Permissions { get; } =
    [
        new(BudgetPermissions.Edit, "Budget_Permission_Edit_Title", "Budget_Permission_Edit_Blurb",
            [TeamOwner.Kind], TopLevelOnly: true, AdminRoles: [FinanceRoles.FinanceAdmin], typeof(BudgetResource)),
    ];
}
