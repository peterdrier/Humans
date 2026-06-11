using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Tickets;
using Humans.Infrastructure.Repositories.Budget;
using BudgetBudgetService = Humans.Application.Services.Budget.BudgetService;
using TicketsTicketingBudgetService = Humans.Application.Services.Tickets.TicketingBudgetService;

namespace Humans.Web.Extensions.Sections;

internal static class BudgetSectionExtensions
{
    internal static IServiceCollection AddBudgetSection(this IServiceCollection services)
    {
        services.AddSingleton<IBudgetRepository, BudgetRepository>();
        services.AddScoped<BudgetBudgetService>();
        services.AddScoped<IBudgetService>(sp => sp.GetRequiredService<BudgetBudgetService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BudgetBudgetService>());

        services.AddScoped<ITicketingBudgetService, TicketsTicketingBudgetService>();

        return services;
    }
}
