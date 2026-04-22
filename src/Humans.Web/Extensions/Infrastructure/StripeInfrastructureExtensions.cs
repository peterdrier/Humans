using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Infrastructure;

internal static class StripeInfrastructureExtensions
{
    internal static IServiceCollection AddStripeInfrastructure(this IServiceCollection services)
    {
        // Stripe integration (read-only — fee tracking and payment method attribution)
        services.Configure<StripeSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("STRIPE_API_KEY") ?? string.Empty;
        });
        services.AddScoped<IStripeService, StripeService>();

        return services;
    }
}
