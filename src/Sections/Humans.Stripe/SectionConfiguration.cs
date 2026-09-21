using Humans.Base.Configuration;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Humans.Stripe;

/// <summary>
/// Stripe's own settings — the same values <c>Section.Register</c> binds into
/// <c>StripeSettings</c>. One key per account/purpose; production keys must be Restricted API
/// Keys (rk_*).
/// </summary>
internal sealed class SectionConfiguration : ISectionConfiguration
{
    public void DeclareSettings(IConfiguration configuration, ConfigurationRegistry registry)
    {
        // Webhook cleanup (operational — the GitHub repo that runs the cleanup workflow).
        configuration.GetOptionalSetting(registry, "Stripe:WebhookCleanupOwner", "Stripe (Store)");
        configuration.GetOptionalSetting(registry, "Stripe:WebhookCleanupRepository", "Stripe (Store)");

        registry.RegisterEnvironmentVariable("STRIPE_TICKETS_KEY", "Stripe (Tickets)", isSensitive: true);
        registry.RegisterEnvironmentVariable("STRIPE_STORE_KEY", "Stripe (Store)", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        registry.RegisterEnvironmentVariable("STRIPE_STORE_WEBHOOK_SECRET", "Stripe (Store)", isSensitive: true,
            importance: ConfigurationImportance.Recommended);
        // Ephemeral envs only (PR previews) — auto-registers the webhook at boot. Never set in QA/prod.
        registry.RegisterEnvironmentVariable("STRIPE_STORE_WEBHOOK_REGISTRAR_KEY", "Stripe (Store, PR-preview only)",
            isSensitive: true);
    }
}
