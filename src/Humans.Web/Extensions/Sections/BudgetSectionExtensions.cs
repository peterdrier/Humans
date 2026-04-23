using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Infrastructure.Repositories.Budget;
using Humans.Infrastructure.Repositories.Tickets;
using Humans.Infrastructure.Services;
using BudgetBudgetService = Humans.Application.Services.Budget.BudgetService;
using TicketsTicketingBudgetService = Humans.Application.Services.Tickets.TicketingBudgetService;

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

        // TicketingBudgetService (§15 Part 1 — Tickets bridge, issue #545)
        // Application-layer service goes through the narrow ITicketingBudgetRepository
        // for paid-order reads and delegates all Budget-owned mutations to IBudgetService.
        // Repository is Singleton (IDbContextFactory-based) per design-rules §15b.
        services.AddSingleton<ITicketingBudgetRepository, TicketingBudgetRepository>();
        services.AddScoped<ITicketingBudgetService, TicketsTicketingBudgetService>();

        return services;
    }
}
