using Humans.Finance.Contracts;
using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Budget.Authorization;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Jobs;
using Humans.Budget.Services;
using Humans.Base.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Budget;

public sealed class Section : ISection, IIssueQueueOwner
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
}
