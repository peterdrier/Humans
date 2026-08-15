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
/// The Holded HTTP client is <em>not</em> registered here. <c>IHoldedClient</c> belongs to the
/// Holded section, which registers it; Finance consumes it through
/// <c>Humans.Holded.Contracts</c>. <c>HoldedSyncJob</c> stays in
/// <c>Humans.Infrastructure/Jobs</c> because Hangfire serializes the declaring type name of a
/// scheduled job — it is a shim over Holded's <c>IHoldedNightlySync</c>, which calls this
/// section's <c>IHoldedFinanceService.SyncAsync</c> first.
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
