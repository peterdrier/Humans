using Humans.Application.Configuration;
using Humans.Infrastructure.Configuration;

namespace Humans.Web.Extensions.Infrastructure;

internal static class ConfigurationMetadataExtensions
{
    internal static IServiceCollection AddConfigurationMetadata(
        this IServiceCollection services,
        IConfiguration configuration,
        ConfigurationRegistry? configRegistry)
    {
        // Register all infrastructure config keys in the registry for the Admin Configuration page
        if (configRegistry is not null)
        {
            // Email settings
            configuration.GetRequiredSetting(configRegistry, "Email:SmtpHost", "Email");
            configuration.GetOptionalSetting(configRegistry, "Email:Username", "Email", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "Email:Password", "Email", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configuration.GetRequiredSetting(configRegistry, "Email:FromAddress", "Email");
            configuration.GetRequiredSetting(configRegistry, "Email:BaseUrl", "Email");
            configuration.GetOptionalSetting(configRegistry, "Email:DpoAddress", "Email",
                importance: ConfigurationImportance.Recommended);

            // GitHub settings
            configuration.GetRequiredSetting(configRegistry, "GitHub:Owner", "GitHub");
            configuration.GetRequiredSetting(configRegistry, "GitHub:Repository", "GitHub");
            configuration.GetRequiredSetting(configRegistry, "GitHub:AccessToken", "GitHub", isSensitive: true);

            // Guide settings
            configuration.GetOptionalSetting(configRegistry, "Guide:Owner", "Guide");
            configuration.GetOptionalSetting(configRegistry, "Guide:Repository", "Guide");
            configuration.GetOptionalSetting(configRegistry, "Guide:Branch", "Guide");
            configuration.GetOptionalSetting(configRegistry, "Guide:FolderPath", "Guide");
            configuration.GetOptionalSetting(configRegistry, "Guide:CacheTtlHours", "Guide");
            configuration.GetOptionalSetting(configRegistry, "Guide:AccessToken", "Guide", isSensitive: true);

            // Google Maps
            configuration.GetRequiredSetting(configRegistry, "GoogleMaps:ApiKey", "Google Maps", isSensitive: true);

            // Google Workspace
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:ServiceAccountKeyPath", "Google Workspace",
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:ServiceAccountKeyJson", "Google Workspace",
                isSensitive: true, importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:Domain", "Google Workspace",
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "GoogleWorkspace:CustomerId", "Google Workspace",
                importance: ConfigurationImportance.Recommended);

            // Ticket Vendor
            configuration.GetOptionalSetting(configRegistry, "TicketVendor:EventId", "Ticket Vendor");
            configuration.GetOptionalSetting(configRegistry, "TicketVendor:Provider", "Ticket Vendor");
            configuration.GetOptionalSetting(configRegistry, "TicketVendor:SyncIntervalMinutes", "Ticket Vendor");

            // MailerLite — env var is the production path; dotted key is the dev/user-secrets path.
            configuration.GetOptionalSetting(configRegistry, "MailerLite:ApiKey", "MailerLite", isSensitive: true,
                importance: ConfigurationImportance.Recommended);

            // Holded (Expenses) — env var is the production path; BaseUrl has a default.
            configuration.GetOptionalSetting(configRegistry, "Holded:BaseUrl", "Holded");

            // Anthropic (Agent section)
            configuration.GetOptionalSetting(configRegistry, "Anthropic:ApiKey", "Anthropic", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "Anthropic:DefaultModel", "Anthropic");

            // SEPA — read by Expenses payment file generation. Without these, payment files are unusable.
            configuration.GetOptionalSetting(configRegistry, "Sepa:CreditorName", "SEPA",
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "Sepa:CreditorIban", "SEPA", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "Sepa:CreditorBic", "SEPA",
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "Sepa:CreditorIdentifier", "SEPA",
                importance: ConfigurationImportance.Recommended);
            configuration.GetOptionalSetting(configRegistry, "Sepa:ChargeBearer", "SEPA");

            // City Planning team slug — without it, only admins can edit polygons.
            configuration.GetOptionalSetting(configRegistry, "CityPlanning:CityPlanningTeamSlug", "City Planning");

            // Team Resource Management toggle
            configuration.GetOptionalSetting(configRegistry, "TeamResourceManagement:AllowCoordinatorsToManageResources",
                "Teams");

            // Stripe webhook cleanup (operational — GitHub repo that runs the cleanup workflow)
            configuration.GetOptionalSetting(configRegistry, "Stripe:WebhookCleanupOwner", "Stripe (Store)");
            configuration.GetOptionalSetting(configRegistry, "Stripe:WebhookCleanupRepository", "Stripe (Store)");

            // Dev auth
            configuration.GetOptionalSetting(configRegistry, "DevAuth:Enabled", "Development");

            // Environment variable secrets
            configRegistry.RegisterEnvironmentVariable("FEEDBACK_API_KEY", "Feedback API", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("ISSUES_API_KEY", "Issues API", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("TICKET_VENDOR_API_KEY", "Ticket Vendor", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configRegistry.RegisterEnvironmentVariable("LOG_API_KEY", "Log API", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("AGENT_API_KEY", "Agent API", isSensitive: true);
            // MailerLite flat env-var fallback — primary path in PR/prod (Coolify can't use dotted keys).
            configRegistry.RegisterEnvironmentVariable("MAILERLITE_API_KEY", "MailerLite", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            // Holded flat env-var — required for Expenses integration.
            configRegistry.RegisterEnvironmentVariable("HOLDED_API_KEY", "Holded", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            // SEPA IBAN override — env-var wins over Sepa:CreditorIban appsetting.
            configRegistry.RegisterEnvironmentVariable("SEPA_CREDITOR_IBAN", "SEPA", isSensitive: true);
            // Stripe — one key per account/purpose; production keys must be Restricted API Keys (rk_*).
            configRegistry.RegisterEnvironmentVariable("STRIPE_TICKETS_KEY", "Stripe (Tickets)", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("STRIPE_STORE_KEY", "Stripe (Store)", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configRegistry.RegisterEnvironmentVariable("STRIPE_STORE_WEBHOOK_SECRET", "Stripe (Store)", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            // Ephemeral envs only (PR previews) — auto-registers the webhook at boot. Never set in QA/prod.
            configRegistry.RegisterEnvironmentVariable("STRIPE_STORE_WEBHOOK_REGISTRAR_KEY", "Stripe (Store, PR-preview only)", isSensitive: true);
        }

        return services;
    }
}
