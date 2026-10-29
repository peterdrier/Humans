using Humans.Base.Authorization;
using Humans.Base.Constants;
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Budget;

public sealed class Section :
    ISection,
    IIssueQueueOwner,
    ISectionAdminNav,
    ISectionJobs,
    ISectionNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<BudgetDbContext>(sentinelTable: "budget_years");

        services.AddSingleton<IBudgetRepository, BudgetRepository>();
        services.AddScoped<BudgetService>();
        services.AddScoped<IBudgetManagement>(sp => sp.GetRequiredService<BudgetService>());
        services.AddScoped<IBudgetService>(sp => sp.GetRequiredService<BudgetService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BudgetService>());

        services.AddScoped<TicketingBudgetService>();
        services.AddScoped<ITicketingBudgetService>(sp => sp.GetRequiredService<TicketingBudgetService>());
        // A factory: the job's constructor is internal, and DI's activation only sees public ones.
        services.AddScoped(sp => new TicketingBudgetSyncJob(
            sp.GetRequiredService<ITicketingBudgetService>(),
            sp.GetRequiredService<IBudgetService>(),
            sp.GetRequiredService<ILogger<TicketingBudgetSyncJob>>()));

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

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "budget-ticketing-sync", typeof(TicketingBudgetSyncJob), "30 4 * * *");
    }

    IEnumerable<MemberNavItem> ISectionNav.Items() =>
        [new("Nav_Budget", Controller: "Budget", Action: "Summary", Policy: PolicyNames.AppAccess, Weight: 70)];
}
