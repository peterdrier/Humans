using Humans.Application.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Budget.Authorization;
using Humans.Budget.Contracts;
using Humans.Budget.Data;
using Humans.Budget.Services;
using Humans.Infrastructure.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Budget;

/// <summary>
/// Budget's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <c>TicketingBudgetSyncJob</c> is <em>not</em> registered here: recurring jobs are named
/// by concrete type in Shell's <c>UseHumansRecurringJobs</c> roll-call and there is no
/// discovery seam for them yet, so it stays in <c>Humans.Infrastructure/Jobs</c> and
/// reaches the section through <see cref="ITicketingBudgetService"/> (design §15.6b).
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

        // Shell's /dev/seed/budget action drives this through the contracts leaf rather than
        // resolving the concrete seeder, which is what keeps Budget's fifteen write methods
        // off the leaf.
        services.AddScoped<IBudgetDemoSeeder, DevelopmentBudgetSeeder>();

        // Resource-based handlers move into the section; the policies they satisfy stay in
        // Shell's AuthorizationPolicyExtensions (design §8's asymmetry, §15 step 6).
        services.AddScoped<IAuthorizationHandler, BudgetAuthorizationHandler>();
    }
}
