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
        // Dual-register: IExpenseReportService and IUserDataContributor resolve to the same instance.
        services.AddScoped<ExpensesExpenseReportService>();
        services.AddScoped<IExpenseReportService>(sp => sp.GetRequiredService<ExpensesExpenseReportService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ExpensesExpenseReportService>());
        services.AddScoped<HoldedExpenseOutboxJob>();
        services.AddScoped<ExpensePaidPollingJob>();

        // SEPA config — bind from appsettings "Sepa" section; allow IBAN override via env var.
        services.Configure<SepaConfig>(opts =>
        {
            config.GetSection("Sepa").Bind(opts);
            var ibanOverride = Environment.GetEnvironmentVariable("SEPA_CREDITOR_IBAN");
            if (!string.IsNullOrEmpty(ibanOverride))
                opts.CreditorIban = ibanOverride;
        });
        services.AddSingleton<ISepaPaymentFileBuilder, SepaPaymentFileBuilder>();

        return services;
    }
}
