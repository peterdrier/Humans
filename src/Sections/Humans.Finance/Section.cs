using Humans.Application.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Services;
using Humans.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Finance;

/// <summary>
/// Finance's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// The Holded HTTP client is <em>not</em> registered here. <c>IHoldedClient</c> is an external
/// API connector with its own section doc, consumed by Expenses as well as Finance, and stays
/// in Base exactly as <c>IStripeService</c> did for Store — Shell still registers it. The same
/// goes for <c>HoldedSyncJob</c>: recurring jobs are named by type in Shell's
/// <c>UseHumansRecurringJobs</c> roll-call, and there is no discovery seam for them yet, so it
/// stays in <c>Humans.Infrastructure/Jobs</c> and reaches Finance through the contract.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<FinanceDbContext>(sentinelTable: "holded_expense_docs");

        services.AddScoped<IHoldedRepository, Repository>();
        services.AddScoped<Service>();
        services.AddScoped<IHoldedFinanceService>(sp => sp.GetRequiredService<Service>());
        // Owns the user-scoped holded_creditor_contacts table → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<Service>());
    }
}
