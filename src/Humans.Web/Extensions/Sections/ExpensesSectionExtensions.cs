using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Expenses;
using Humans.Infrastructure.Repositories.Expenses;
using Humans.Infrastructure.Services.Expenses;

namespace Humans.Web.Extensions.Sections;

internal static class ExpensesSectionExtensions
{
    internal static IServiceCollection AddExpensesSection(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ExpenseAttachmentFilesystemStorageOptions>(
            config.GetSection(ExpenseAttachmentFilesystemStorageOptions.Section));
        services.AddSingleton<IExpenseAttachmentStorageService,
            ExpenseAttachmentFilesystemStorage>();
        services.AddSingleton<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<IExpenseReportService, ExpenseReportService>();
        return services;
    }
}
