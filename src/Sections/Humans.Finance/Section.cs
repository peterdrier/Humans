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
/// <c>Humans.Holded.Contracts</c>. <c>HoldedSyncJob</c> is not registered here either, and is
/// not this section's: it is a shim over Holded's <c>IHoldedNightlySync</c>, which calls this
/// section's <c>IHoldedDocService.SyncAsync</c> first, and it lives in
/// <c>Humans.Holded/Jobs/</c> since the HUM0034 Jobs carve-out.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<FinanceDbContext>(sentinelTable: "holded_expense_docs");

        services.AddScoped<IHoldedRepository, Repository>();

        // Each service is registered once and exposed twice: the public contract other sections
        // resolve, and the wider internal one FinanceController resolves.
        services.AddScoped<HoldedDocService>();
        services.AddScoped<IHoldedDocService>(sp => sp.GetRequiredService<HoldedDocService>());
        services.AddScoped<IHoldedDocAdminService>(sp => sp.GetRequiredService<HoldedDocService>());

        services.AddScoped<CreditorService>();
        services.AddScoped<ICreditorService>(sp => sp.GetRequiredService<CreditorService>());
        services.AddScoped<ICreditorAdminService>(sp => sp.GetRequiredService<CreditorService>());
        // Owns the user-scoped holded_creditor_contacts table → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<CreditorService>());
    }
}
