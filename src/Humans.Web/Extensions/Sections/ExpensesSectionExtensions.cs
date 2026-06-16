using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories.Expenses;
using ExpensesExpenseReportService = Humans.Application.Services.Expenses.ExpenseReportService;

namespace Humans.Web.Extensions.Sections;

internal static class ExpensesSectionExtensions
{
    internal static IServiceCollection AddExpensesSection(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<ExpensesExpenseReportService>();
        services.AddScoped<IExpenseReportServiceRead>(sp => sp.GetRequiredService<ExpensesExpenseReportService>());
        services.AddScoped<IExpenseReportService>(sp => sp.GetRequiredService<ExpensesExpenseReportService>());
        services.AddScoped<IExpenseReportBackgroundProcessor>(sp => sp.GetRequiredService<ExpensesExpenseReportService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ExpensesExpenseReportService>());
        services.AddScoped<HoldedExpenseOutboxJob>();

        services.Configure<TravelReimbursementConfig>(config.GetSection("TravelReimbursement"));

        return services;
    }
}
