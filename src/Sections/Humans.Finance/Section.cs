using Humans.Base.Authorization;
using Humans.Base.Caching;
using Humans.Base.Configuration;
using Humans.Base.Constants;
using Humans.Base.Helpers;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Jobs;
using Humans.Finance.Services;
using Humans.Finance.ViewComponents;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Finance;

public sealed class Section :
    ISection,
    ISectionRoles,
    ISectionAdminNav,
    ISectionConfiguration,
    ISectionDataMigrations,
    ISectionJobs,
    ISectionPolicies,
    IUserPart
{
    IReadOnlyList<RoleDefinition> ISectionRoles.Roles { get; } =
    [
        new(FinanceRoles.FinanceAdmin, "Finance Admin",
            "Stewards of the purse. They manage budgets, track expenditures, and ensure every euro is accounted for with the transparency a nonprofit deserves.",
            "fa-solid fa-coins"),
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<FinanceDbContext>(sentinelTable: "holded_expense_docs");

        // Absent the Sepa:* keys, /Finance/Creditors says payout is unavailable rather than generating a rejectable
        // file. The flat SEPA_CREDITOR_IBAN overrides the appsetting (not a fallback): this is the account the money
        // leaves, so the deployment-set value wins.
        services
            .Configure<SepaOptions>(configuration.GetSection("Sepa"))
            .Configure<SepaOptions>(opts =>
            {
                var flat = Environment.GetEnvironmentVariable("SEPA_CREDITOR_IBAN");
                if (!string.IsNullOrWhiteSpace(flat))
                    opts.CreditorIban = flat;
            });

        services.AddScoped<IHoldedRepository, Repository>();
        // Sweep failure counts must outlive scoped requests and jobs.
        services.AddSingleton(sp => new TrackedCache<string, Dictionary<string, int>>(
            "Finance.SepaStuckBankLines", warmOnStartup: false,
            sp.GetRequiredService<ILogger<Service>>()));
        services.AddSingleton<ICacheStats>(sp =>
            sp.GetRequiredService<TrackedCache<string, Dictionary<string, int>>>());
        services.AddScoped<Service>();
        services.AddScoped<IHoldedFinanceManagement>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IHoldedFinanceService>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IHoldedFinanceWrite>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IHoldedFinanceAdminService>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<ISepaBankBooking>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<Jobs.SepaBankBookingJob>();
        services.AddScoped<Jobs.HoldedDocSyncJob>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<Service>());

        services.AddScoped<IPaymentDetailsRepository, PaymentDetailsRepository>();
        services.AddScoped<PaymentDetailsService>();
        services.AddScoped<IPaymentDetailsManagement>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IFinanceService>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IFinanceWrite>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<PaymentDetailsService>());

        services.AddScoped<FinanceEmails>();
        services.AddScoped<IEmailPreviewContributor, FinanceEmailPreviews>();

        services.AddScoped<CashFlowService>();
        services.AddScoped<CreditorMatchService>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Finance", [
            new("Holded connector", "Finance", "Holded", null, null, "fa-solid fa-plug", PolicyNames.FinanceAdminOrAdmin)
        ])
    ];

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Sepa:CreditorName", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:CreditorIban", "SEPA", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:CreditorBic", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:CreditorIdentifier", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:ChargeBearer", "SEPA");
        // Defaults to €50 when unset.
        configuration.GetOptionalSetting(registry, "Sepa:MaxPayoutPerTransfer", "SEPA");
        // Either unset, /Finance/Sepa offers no Book buttons.
        configuration.GetOptionalSetting(registry, "Sepa:TreasuryAccountId", "SEPA",
            importance: ConfigurationImportance.Recommended);
        configuration.GetOptionalSetting(registry, "Sepa:TreasuryLedgerAccount", "SEPA",
            importance: ConfigurationImportance.Recommended);

        // Wins over the Sepa:CreditorIban appsetting.
        registry.RegisterEnvironmentVariable("SEPA_CREDITOR_IBAN", "SEPA", isSensitive: true);
    }

    internal const string IbanToPaymentDetailsKey = "finance.iban-to-payment-details";

    IEnumerable<DataMigration> ISectionDataMigrations.Migrations() =>
    [
        new(IbanToPaymentDetailsKey, "Finance",
            "Copy each member's IBAN from profiles.Iban into finance_payment_details",
            "PaymentDetailsService's legacy fallback and ProfileInfo.Iban; then a drop PR for profiles.Iban",
            IbanStatusAsync, ApplyIbanAsync),
    ];

    // Masked: this screen lists every member at once; an unmasked IBAN is shown one member at a time, behind the
    // audited reveal.
    private static async Task<DataMigrationStatus> IbanStatusAsync(IServiceProvider sp, CancellationToken ct) =>
        new(["User", "Burner name", "IBAN"],
            [.. (await sp.GetRequiredService<IPaymentDetailsManagement>().GetUncopiedLegacyIbansAsync(ct))
                .Select(r => (IReadOnlyList<string>)[r.UserId.ToString(), r.BurnerName, IbanFormatter.Mask(r.Iban)])]);

    private static Task<int> ApplyIbanAsync(IServiceProvider sp, Guid actorUserId, CancellationToken ct) =>
        sp.GetRequiredService<IPaymentDetailsManagement>().CopyLegacyIbansAsync(ct);

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        // The Sabadell line lands 6-50 h after a file is generated, so hourly buys nothing; :17 avoids the on-the-hour
        // stampede.
        yield return new RecurringJobDescriptor(
            "sepa-bank-booking", typeof(SepaBankBookingJob), "17 */2 * * *");

        // Ahead of Holded's 03:00 ledger sweep; the shared "holded-api" lock keeps them apart if this runs long.
        yield return new RecurringJobDescriptor(
            "holded-doc-sync", typeof(HoldedDocSyncJob), "45 2 * * *");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.FinanceAdminOrAdmin, policy =>
            policy.RequireRole(FinanceRoles.FinanceAdmin, RoleNames.Admin));
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
        [new(UserPartSlots.AdminDetailSidebar, typeof(PaymentDetailsViewComponent), Weight: 100)];
}
