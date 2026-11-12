using Humans.Base.Authorization;
using Humans.Base.Caching;
using Humans.Base.Configuration;
using Humans.Base.Constants;
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
using Humans.AuditLog.Contracts;

namespace Humans.Finance;

public sealed class Section :
    ISection,
    ISectionRoles,
    ISectionAdminNav,
    ISectionConfiguration,
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
        services.AddSingleton<IAuditActionContributor, FinanceAuditActions>();
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
        services.AddScoped<IFinanceService>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IFinanceWrite>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<IFinanceAdminService>(sp => sp.GetRequiredService<Service>());
        services.AddScoped<Jobs.SepaBankBookingJob>();
        services.AddScoped<Jobs.ExpenseReconJob>();
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<Service>());

        services.AddScoped<IPaymentDetailsRepository, PaymentDetailsRepository>();
        services.AddScoped<PaymentDetailsService>();
        services.AddScoped<IPaymentDetailsManagement>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<PaymentDetailsService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<PaymentDetailsService>());

        services.AddScoped<FinanceEmails>();
        services.AddScoped<IEmailPreviewContributor, FinanceEmailPreviews>();

        services.AddScoped<CashFlowService>();
        services.AddScoped<FinanceOverviewService>();
        services.AddScoped<PleoBooking>();
        // Both outlive a request: the run keeps going after the page is left, and its holds must span runs.
        services.AddSingleton<PleoBookingState>();
        services.AddSingleton<PleoBookingRunner>();
        services.AddSingleton<SepaBookingRunner>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Finance", [
            new("Overview", "Finance", "Index", null, null, "fa-solid fa-landmark", FinancePolicies.Read),
            new("Cash flow", "Finance", "CashFlow", null, null, "fa-solid fa-chart-line", FinancePolicies.Read),
            new("Creditors", "Finance", "Creditors", null, null, "fa-solid fa-users", FinancePolicies.Read),
            new("SEPA payouts", "Finance", "Sepa", null, null, "fa-solid fa-money-bill-transfer", FinancePolicies.Read),
            new("Pleo reconcile", "Finance", "Pleo", null, null, "fa-solid fa-credit-card", FinancePolicies.Read),
            new("Expense recon", "Finance", "ExpenseRecon", null, null, "fa-solid fa-scale-balanced", FinancePolicies.Read),
            new("Account map", "Finance", "HoldedAccounts", null, null, "fa-solid fa-diagram-project", FinancePolicies.Read),
            new("Unmatched docs", "Finance", "HoldedUnmatched", null, null, "fa-solid fa-question", FinancePolicies.Read)
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

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        // The Sabadell line lands 6-50 h after a file is generated, so hourly buys nothing; :17 avoids the on-the-hour
        // stampede.
        yield return new RecurringJobDescriptor(
            "sepa-bank-booking", typeof(SepaBankBookingJob), "17 */2 * * *");

        // After Holded's nightly sync (03:00: purchase documents, then the ledger), so both mirrors it reads are fresh.
        yield return new RecurringJobDescriptor(
            "expense-recon", typeof(ExpenseReconJob), "30 3 * * *");
    }

    void ISectionPolicies.AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(FinancePolicies.Read, policy =>
            policy.RequireRole(FinanceRoles.FinanceAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(FinancePolicies.Manage, policy =>
            policy.RequireRole(FinanceRoles.FinanceAdmin, RoleNames.Admin));
    }

    IEnumerable<UserPart> IUserPart.Parts() =>
        [new(UserPartSlots.AdminDetailSidebar, typeof(PaymentDetailsViewComponent), Weight: 100)];
}
