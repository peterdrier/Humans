using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Services;
using Humans.Base.Hosting;
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
/// section's <c>IHoldedFinanceService.SyncAsync</c> first, and it lives in
/// <c>src/Sections/Humans.Holded/Jobs/</c> since G5 lane 5b-5 (nobodies-collective/Humans#866).
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<FinanceDbContext>(sentinelTable: "holded_expense_docs");

        // The organisation's SEPA identity. Keys are the pre-existing Sepa:* ones; absent them,
        // /Finance/Creditors says payout is unavailable rather than generating a rejectable file.
        // Second pass honours the flat SEPA_CREDITOR_IBAN, registered in ConfigurationMetadata-
        // Extensions as winning over the appsetting, for deployments that cannot use dotted keys —
        // same shape as MailerLite's MAILERLITE_API_KEY. It overrides rather than falls back: this
        // is the account the money leaves, so the deployment-set value beats the committed one.
        services
            .Configure<SepaOptions>(configuration.GetSection("Sepa"))
            .Configure<SepaOptions>(opts =>
            {
                var flat = Environment.GetEnvironmentVariable("SEPA_CREDITOR_IBAN");
                if (!string.IsNullOrWhiteSpace(flat))
                    opts.CreditorIban = flat;
            });

        services.AddScoped<IHoldedRepository, Repository>();
        services.AddScoped<Service>();
        services.AddScoped<IHoldedFinanceService>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IHoldedFinanceServiceRead>(sp => sp.GetRequiredService<Service>());
        // /Finance/Holded's read model. Internal — this section's own screen is the only consumer.
        services.AddScoped<IHoldedFinanceAdminService>(sp => sp.GetRequiredService<Service>());
        // Owns the user-scoped holded_creditor_contacts table → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<Service>());
    }
}
