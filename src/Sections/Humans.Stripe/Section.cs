using Humans.Base.Interfaces;
using Humans.Stripe.Contracts;
using Humans.Stripe.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Stripe;

/// <summary>
/// <c>SectionConfiguration</c> declares the same <c>STRIPE_*</c> variables to the settings inventory: rename both
/// together.
/// </summary>
public sealed class Section : ISection
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
    }
}
