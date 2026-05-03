using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Infrastructure;

internal static class StripeInfrastructureExtensions
{
    internal static IServiceCollection AddStripeInfrastructure(this IServiceCollection services)
    {
        // Stripe integration. One key per Stripe account; production keys must be Restricted API Keys (rk_*)
        // scoped to the minimum permissions used. Refunds/payouts/chargebacks remain dashboard-manual.
        //   - STRIPE_TICKETS_KEY: Tickets-account key (PI/balance reads for fee enrichment).
        //     STRIPE_API_KEY is honored as a deprecated fallback and will be removed in a follow-up PR.
        //   - STRIPE_STORE_KEY: Store-account key (checkout_session:write).
        //   - STRIPE_STORE_WEBHOOK_SECRET: Store webhook signing secret.
        services.Configure<StripeSettings>(opts =>
        {
            var ticketsKey = Environment.GetEnvironmentVariable("STRIPE_TICKETS_KEY") ?? string.Empty;
            if (string.IsNullOrEmpty(ticketsKey))
            {
                var deprecated = Environment.GetEnvironmentVariable("STRIPE_API_KEY") ?? string.Empty;
                if (!string.IsNullOrEmpty(deprecated))
                {
                    ticketsKey = deprecated;
                    opts.TicketsKeyFromDeprecatedFallback = true;
                }
            }
            opts.TicketsKey = ticketsKey;
            opts.StoreKey = Environment.GetEnvironmentVariable("STRIPE_STORE_KEY") ?? string.Empty;
            opts.StoreWebhookSecret = Environment.GetEnvironmentVariable("STRIPE_STORE_WEBHOOK_SECRET") ?? string.Empty;
        });
        services.AddScoped<IStripeService, StripeService>();
        services.AddHostedService<StripeStartupSmokeService>();

        return services;
    }
}
