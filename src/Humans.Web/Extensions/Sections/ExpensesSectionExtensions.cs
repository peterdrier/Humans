using Humans.Application.Interfaces.Expenses;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Infrastructure.Jobs;
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
