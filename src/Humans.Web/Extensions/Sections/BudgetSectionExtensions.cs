using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories;
using Humans.Infrastructure.Services;
using BudgetBudgetService = Humans.Application.Services.Budget.BudgetService;

namespace Humans.Web.Extensions.Sections;

internal static class BudgetSectionExtensions
{
    internal static IServiceCollection AddBudgetSection(this IServiceCollection services)
    {
        // Budget section — §15 repository pattern (issue #544).
        // No caching decorator: Budget pages are admin-only and low-traffic.
        // BudgetRepository uses scoped HumansDbContext (like ApplicationRepository)
        // since the service stages multi-entity writes (year + groups + categories +
        // audit log) and commits them through a single SaveChanges for atomicity.
        services.AddScoped<IBudgetRepository, BudgetRepository>();
        services.AddScoped<BudgetBudgetService>();
        services.AddScoped<IBudgetService>(sp => sp.GetRequiredService<BudgetBudgetService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BudgetBudgetService>());

        services.AddScoped<ITicketingBudgetService, TicketingBudgetService>();

        return services;
    }
}
