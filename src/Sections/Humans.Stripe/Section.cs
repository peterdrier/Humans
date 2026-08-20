using Humans.Base.Interfaces;
using Humans.Stripe.Contracts;
using Humans.Stripe.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Stripe;

/// <summary>
/// The payments connector's DI entry point, moved verbatim from Shell's
/// <c>StripeInfrastructureExtensions.AddStripeInfrastructure</c>.
/// </summary>
/// <remarks>
/// The whole registration is the section's own: <c>StripeSettings</c> binds only
/// <c>STRIPE_*</c> environment variables and <c>Stripe:*</c> configuration, and its three
/// consumers (the service and the two hosted services) are all <c>internal</c> here — so
/// unlike <c>TicketVendorSettings</c>, no line of it belongs to anyone else (Governance's
/// rule: the section that owns the file is not always the section that owns the line —
/// here it is). Shell keeps the <c>ConfigurationRegistry</c> metadata for the same
/// variables, which is Shell's settings-inventory page, not a binding.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Stripe integration. One key per Stripe account / purpose; production keys must be Restricted API
        // Keys (rk_*) scoped to the minimum permissions used. Refunds/payouts/chargebacks remain dashboard-manual.
        //   - STRIPE_TICKETS_KEY: Tickets-account key (PI/balance reads for fee enrichment).
        //   - STRIPE_STORE_KEY: Store-account key (checkout_session:write only).
        //   - STRIPE_STORE_WEBHOOK_SECRET: Store webhook signing secret. Set manually in QA/prod;
        //     overwritten at boot in ephemeral envs that auto-register (see below).
        //   - STRIPE_STORE_WEBHOOK_REGISTRAR_KEY: dedicated key for boot-time webhook auto-registration in
        //     ephemeral envs (PR previews). Carries webhook_endpoint:read/write scope. Kept separate from
        //     STRIPE_STORE_KEY so PR-preview testing exercises the production-scoped checkout path with the
        //     same narrow scope production has. NEVER set in QA or production.
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
    }
}
