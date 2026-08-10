using Humans.Application.Interfaces.Gdpr;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Domain;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Humans.Infrastructure.Hosting;
using Humans.UI.Models.Tables;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Application.Interfaces;
using Humans.Expenses.Authorization;

namespace Humans.Expenses;

/// <summary>
/// Expenses' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// The Holded HTTP client is <em>not</em> registered here. <c>IHoldedClient</c> is an external
/// API connector with its own section doc and stays in Base, consumed by Expenses and Finance
/// alike (memory/architecture/vendor-connectors-own-sections.md). Nor is
/// <c>HoldedExpenseOutboxJob</c>: recurring jobs are named by concrete type in Shell's
/// <c>UseHumansRecurringJobs</c> roll-call and there is no discovery seam for them yet, so it
/// stays in <c>Humans.Infrastructure/Jobs</c> and reaches the section through
/// <c>IExpenseReportBackgroundProcessor</c> (design §15.6b).
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<ExpensesDbContext>(sentinelTable: "expense_reports");

        services.AddSingleton<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<ExpenseReportService>();
        services.AddScoped<IExpenseReportServiceRead>(sp => sp.GetRequiredService<ExpenseReportService>());
        services.AddScoped<IExpenseReportService>(sp => sp.GetRequiredService<ExpenseReportService>());
        services.AddScoped<IExpenseReportBackgroundProcessor>(sp => sp.GetRequiredService<ExpenseReportService>());
        // Owns the user-scoped expense_reports table → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<ExpenseReportService>());

        services.Configure<TravelReimbursementConfig>(configuration.GetSection("TravelReimbursement"));

        // Resource-based handlers move into the section; the policies they satisfy stay in
        // Shell's AuthorizationPolicyExtensions (design §8's asymmetry, §15 step 6).
        services.AddScoped<IAuthorizationHandler, ExpenseReportAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, IbanAccessHandler>();

        // The section owns its badge colours rather than Humans.UI holding a literal row per
        // section enum: Base cannot name ExpenseReportStatus once the enum moves in here, and
        // referencing the section's contracts leaf from Base to get it back is the trap that
        // ends with Base knowing every section's vocabulary (Peter, 2026-08-09 —
        // memory/architecture/base-ui-registries-are-section-populated.md).
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [ExpenseReportStatus.Draft] = "bg-secondary",
            [ExpenseReportStatus.Submitted] = "bg-primary",
            [ExpenseReportStatus.CoordinatorEndorsed] = "bg-info text-dark",
            [ExpenseReportStatus.Approved] = "bg-success",
            [ExpenseReportStatus.Withdrawn] = "bg-secondary",
        });
    }
}
