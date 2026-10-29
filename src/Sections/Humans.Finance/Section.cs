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
using Humans.AuditLog.Contracts;

namespace Humans.Finance;

public sealed class Section : ISection, ISectionRoles
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
    }
}
