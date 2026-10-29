using System.Globalization;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Models.Tables;
using Humans.Email.Contracts;
using Humans.Expenses.Authorization;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Jobs;
using Humans.Expenses.Models;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Humans.Gdpr.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Expenses;

public sealed class Section :
    ISection,
    ISectionAdminNav,
    ISectionAdminTiles,
    ISectionJobs,
    ISectionPolicies
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

        // Backdoor reaches it through PolicyNames.ExpenseReportView (ISectionPolicies on Section).
        services.AddScoped<IAuthorizationHandler, ExpenseReportAuthorizationHandler>();

        // Base cannot name ExpenseReportStatus, so the section registers its own badge colours.
        EnumBadgeMap.Register(
            Enum.GetValues<ExpenseReportStatus>().ToDictionary(s => (Enum)s, s => s.GetBadgeClass()));

        services.AddScoped<HoldedExpenseOutboxJob>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Expenses", [
            // The finance-admin door to the review queue, so it keeps the admin policy. Members reach the same page
            // from /Expenses.
            new("Review", "Expenses", "Review", null, null, "fa-solid fa-magnifying-glass-dollar", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("expenses.reports", "Expense reports", "fa-solid fa-receipt", ReportsAsync,
            Policy: PolicyNames.FinanceAdminOrAdmin, Weight: 130)
    ];

    private static async ValueTask<AdminTileValue?> ReportsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var reports = await sp.GetRequiredService<IExpenseReportManagement>().GetAllAsync(ct);
        return new AdminTileValue(
            reports.Count.ToString("N0", CultureInfo.CurrentCulture),
            Detail: $"€{reports.Sum(r => r.Total):N0} total, all statuses");
    }

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "expenses-holded-outbox", typeof(HoldedExpenseOutboxJob), "*/5 * * * *");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        // Registered here so Backdoor reaches the same View check ExpensesController uses without the requirement type
        // leaving this section.
        options.AddPolicy(PolicyNames.ExpenseReportView, policy =>
            policy.AddRequirements(new ExpenseReportOperationRequirement(ExpenseReportOperation.View)));
    }
}
