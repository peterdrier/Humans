using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Services;
using Humans.Base.Hosting;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
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
/// <c>Humans.Holded.Contracts</c>. The purchase-doc sync's nightly job, <c>HoldedDocSyncJob</c>, is
/// this section's (<c>Jobs/</c>); the ledger mirror's <c>HoldedSyncJob</c> is Holded's.
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
        // Sweep failure counts outlive scoped requests and jobs; expose their diagnostics.
        services.AddSingleton(sp => new TrackedCache<string, Dictionary<string, int>>(
            "Finance.SepaStuckBankLines", warmOnStartup: false,
            sp.GetRequiredService<ILogger<Service>>()));
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<TrackedCache<string, Dictionary<string, int>>>());
        services.AddScoped<Service>();
        services.AddScoped<IHoldedFinanceManagement>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IHoldedFinanceService>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IHoldedFinanceWrite>(sp => sp.GetRequiredService<Service>());
        // /Finance/Holded's read model. Internal — this section's own screen is the only consumer.
        services.AddScoped<IHoldedFinanceAdminService>(sp => sp.GetRequiredService<Service>());
        // The SEPA bank-line sweep's body; its Hangfire target is SepaBankBookingJob, in this
        // project's Jobs/ (nobodies-collective/Humans#1185).
        services.AddScoped<ISepaBankBooking>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<Jobs.SepaBankBookingJob>();
        services.AddScoped<Jobs.HoldedDocSyncJob>();
        // Owns the user-scoped holded_creditor_contacts table → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<Service>());

        // finance_payment_details: the member IBAN other sections read and write, its export,
        // erasure and account-merge fold (Users decomposition commit 1).
        services.AddScoped<IPaymentDetailsRepository, PaymentDetailsRepository>();
        services.AddScoped<PaymentDetailsService>();
        services.AddScoped<IPaymentDetailsManagement>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IFinanceService>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IFinanceWrite>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<PaymentDetailsService>());

        // Finance owns its email copy and its gallery samples; Email keeps the mechanics
        // (memory/architecture/email-templates-live-in-sender.md).
        services.AddScoped<FinanceEmails>();
        services.AddScoped<IEmailPreviewContributor, FinanceEmailPreviews>();

        // /Finance/CashFlow: reads Budget and Tickets through their public contracts; no tables.
        services.AddScoped<CashFlowService>();
    }
}
