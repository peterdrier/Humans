using Humans.Gdpr.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Jobs;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Humans.Base.Hosting;
using Humans.Email.Contracts;
using Humans.Base.Models.Tables;
using Humans.Expenses.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Base.Interfaces;
using Humans.Expenses.Authorization;

namespace Humans.Expenses;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<ExpensesDbContext>(sentinelTable: "expense_reports");

        services.AddSingleton<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<ExpenseReportService>();
        services.AddScoped<IExpenseReportManagement>(sp => sp.GetRequiredService<ExpenseReportService>());
        services.AddScoped<IExpenseReportService>(sp => sp.GetRequiredService<ExpenseReportService>());
        services.AddScoped<IExpenseReportBackgroundProcessor, ExpenseReportBackgroundProcessor>();
        // Takes nothing from Finance, so Finance can depend on it while ExpenseReportService depends on Finance.
        services.AddScoped<IExpenseHoldedBookingService, ExpenseHoldedBookingService>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ExpenseReportService>());

        services.Configure<TravelReimbursementConfig>(configuration.GetSection("TravelReimbursement"));

        services.AddScoped<ExpensesEmails>();
        services.AddScoped<IEmailPreviewContributor, ExpensesEmailPreviews>();

        // Backdoor reaches it through PolicyNames.ExpenseReportView (SectionPolicies).
        services.AddScoped<IAuthorizationHandler, ExpenseReportAuthorizationHandler>();

        // Base cannot name ExpenseReportStatus, so the section registers its own badge colours.
        EnumBadgeMap.Register(
            Enum.GetValues<ExpenseReportStatus>().ToDictionary(s => (Enum)s, s => s.GetBadgeClass()));

        services.AddScoped<HoldedExpenseOutboxJob>();
    }
}
