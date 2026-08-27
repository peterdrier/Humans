using Humans.Gdpr.Contracts;
using Humans.Expenses.Contracts;
using Humans.Expenses.Data;
using Humans.Expenses.Jobs;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Humans.Base.Hosting;
using Humans.Base.Models.Tables;
using Humans.Expenses.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Base.Interfaces;
using Humans.Expenses.Authorization;

namespace Humans.Expenses;

/// <summary>
/// Expenses' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// The Holded HTTP client is <em>not</em> registered here. <c>IHoldedClient</c> belongs to the
/// Holded section, which registers it; Expenses consumes it through
/// <c>Humans.Holded.Contracts</c> (memory/architecture/vendor-connectors-own-sections.md).
/// <c>HoldedExpenseOutboxJob</c> (<c>Jobs/</c>) drives <c>IExpenseReportBackgroundProcessor</c>;
/// its registration and schedule are contributed via <c>SectionJobs.cs</c>.
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

        // The section owns its badge colours rather than Base holding a literal row per section
        // enum: Base cannot name ExpenseReportStatus, and referencing the section's contracts
        // leaf from Base to get it back is the trap that ends with Base knowing every section's
        // vocabulary (memory/architecture/base-ui-registries-are-section-populated.md).
        // GetBadgeClass is the single source; the section's own views call it directly.
        EnumBadgeMap.Register(
            Enum.GetValues<ExpenseReportStatus>().ToDictionary(s => (Enum)s, s => s.GetBadgeClass()));

        services.AddScoped<HoldedExpenseOutboxJob>();
    }
}
