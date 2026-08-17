using Humans.Application.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Budget.Authorization;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Jobs;
using Humans.Budget.Services;
using Humans.Infrastructure.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Budget;

/// <summary>
/// Budget's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <c>TicketingBudgetSyncJob</c> lives in this project's <c>Jobs/</c> folder since G5
/// lane 5b-3 (nobodies-collective/Humans#866) — the plan had guessed Tickets, but both its
/// collaborators are Budget's and the rows it writes are budget line items. Hangfire scheduling
/// (<c>Add&lt;TicketingBudgetSyncJob&gt;</c>) still lives in Shell's <c>UseHumansRecurringJobs</c>
/// roll-call, but the DI registration moved here (Peter's ruling 43): the job's constructor went
/// <c>internal</c> once <see cref="ITicketingBudgetService"/> did, so only this assembly can build it.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<BudgetDbContext>(sentinelTable: "budget_years");

        services.AddSingleton<IBudgetRepository, BudgetRepository>();
        services.AddScoped<BudgetService>();
        services.AddScoped<IBudgetService>(sp => sp.GetRequiredService<BudgetService>());
        services.AddScoped<IBudgetServiceRead>(sp => sp.GetRequiredService<BudgetService>());
        // Owns the user-attributed budget_audit_logs table → GDPR export contributor
        // (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BudgetService>());

        services.AddScoped<TicketingBudgetService>();
        services.AddScoped<ITicketingBudgetService>(sp => sp.GetRequiredService<TicketingBudgetService>());
        // Factory registration, not plain AddScoped<T>: the job's constructor is internal, so
        // only this assembly can call it — DI's reflection-based activation only sees public ones.
        services.AddScoped(sp => new TicketingBudgetSyncJob(
            sp.GetRequiredService<ITicketingBudgetService>(),
            sp.GetRequiredService<IBudgetServiceRead>(),
            sp.GetRequiredService<ILogger<TicketingBudgetSyncJob>>()));

        // Shell's /dev/seed/budget action drives this through the contracts leaf rather than
        // resolving the concrete seeder, which is what keeps Budget's fifteen write methods
        // off the leaf.
        services.AddScoped<IBudgetDemoSeeder, DevelopmentBudgetSeeder>();

        // Resource-based handlers move into the section; the policies they satisfy stay in
        // Shell's AuthorizationPolicyExtensions (design §8's asymmetry, §15 step 6).
        services.AddScoped<IAuthorizationHandler, BudgetAuthorizationHandler>();
    }
}
