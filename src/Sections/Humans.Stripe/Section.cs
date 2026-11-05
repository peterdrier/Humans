using Humans.Base.Configuration;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Finance.Contracts;
using Humans.Stripe.Contracts;
using Humans.Stripe.Data;
using Humans.Stripe.Jobs;
using Humans.Stripe.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Stripe;

/// <summary>
/// <c>Section</c> (as <c>ISectionConfiguration</c>) declares the same <c>STRIPE_*</c> variables to the settings inventory: rename both
/// together.
/// </summary>
public sealed class Section : ISection, ISectionAdminNav, ISectionConfiguration, ISectionJobs
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StripeSettings>(opts =>
        {
            opts.TicketsKey = Environment.GetEnvironmentVariable("STRIPE_TICKETS_KEY") ?? string.Empty;
            opts.StoreKey = Environment.GetEnvironmentVariable("STRIPE_STORE_KEY") ?? string.Empty;
            opts.StoreWebhookSecret = Environment.GetEnvironmentVariable("STRIPE_STORE_WEBHOOK_SECRET") ?? string.Empty;
            opts.WebhookRegistrarKey = Environment.GetEnvironmentVariable("STRIPE_STORE_WEBHOOK_REGISTRAR_KEY") ?? string.Empty;
            opts.WebhookCleanupGitHubOwner = configuration["Stripe:WebhookCleanupOwner"] ?? string.Empty;
            opts.WebhookCleanupGitHubRepository = configuration["Stripe:WebhookCleanupRepository"] ?? string.Empty;
        });
        services.AddScoped<IStripeService, StripeService>();
        services.AddHostedService<StripeStartupSmokeService>();
        services.AddHostedService<StoreWebhookRegistrationService>();

        services.AddSectionDbContext<StripeDbContext>(sentinelTable: "stripe_balance_transactions");
        services.Configure<StripeMirrorOptions>(opts => StripeMirrorConfiguration.Bind(opts,
            configuration.GetSection(StripeMirrorConfiguration.AccountsKey).Get<List<StripeMirrorAccount>>() ?? []));
        services.AddScoped<IStripeMirrorClient, StripeMirrorClient>();
        services.AddScoped<IStripeMirrorRepository, Repository>();
        services.AddScoped<StripeMirrorService>();
        services.AddScoped<IStripeMirrorAdminService>(sp => sp.GetRequiredService<StripeMirrorService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<StripeMirrorService>());
        services.AddScoped<StripeMirrorSyncJob>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Stripe", [
            new("Stripe", "Stripe", "Index", null, null, "fa-brands fa-stripe-s", FinancePolicies.Read)
        ])
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor("stripe-mirror-sync", typeof(StripeMirrorSyncJob), "30 3 * * *");
    }

    void ISectionConfiguration.DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        configuration.GetOptionalSetting(registry, "Stripe:WebhookCleanupOwner", "Stripe (Store)");
        configuration.GetOptionalSetting(registry, "Stripe:WebhookCleanupRepository", "Stripe (Store)");

        registry.RegisterEnvironmentVariable("STRIPE_TICKETS_KEY", "Stripe (Tickets)", isSensitive: true);
        registry.RegisterEnvironmentVariable("STRIPE_STORE_KEY", "Stripe (Store)", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        registry.RegisterEnvironmentVariable("STRIPE_STORE_WEBHOOK_SECRET", "Stripe (Store)", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        // PR previews only; never set in QA or production.
        registry.RegisterEnvironmentVariable("STRIPE_STORE_WEBHOOK_REGISTRAR_KEY", "Stripe (Store, PR-preview only)",
            isSensitive: true);

        // The read-only mirror: the org key for every account without its own key variable.
        registry.RegisterEnvironmentVariable(StripeMirrorConfiguration.OrgKeyVariable, "Stripe (mirror)", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        foreach (var account in configuration.GetSection(StripeMirrorConfiguration.AccountsKey).GetChildren())
        {
            configuration.GetOptionalSetting(registry, $"{account.Path}:Id", "Stripe (mirror)");
            if (configuration.GetOptionalSetting(registry, $"{account.Path}:KeyVariable", "Stripe (mirror)") is { Length: > 0 } variable)
                registry.RegisterEnvironmentVariable(variable, "Stripe (mirror)", isSensitive: true);
        }
    }
}
