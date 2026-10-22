using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Stripe;

internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
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
    }
}
