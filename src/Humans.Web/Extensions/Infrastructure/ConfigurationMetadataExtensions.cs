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

            // Dev auth
            configuration.GetOptionalSetting(configRegistry, "DevAuth:Enabled", "Development");

            // Environment variable secrets
            configRegistry.RegisterEnvironmentVariable("FEEDBACK_API_KEY", "Feedback API", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("TICKET_VENDOR_API_KEY", "Ticket Vendor", isSensitive: true,
                importance: ConfigurationImportance.Recommended);
            configRegistry.RegisterEnvironmentVariable("LOG_API_KEY", "Log API", isSensitive: true);
            configRegistry.RegisterEnvironmentVariable("STRIPE_API_KEY", "Stripe", isSensitive: true);
        }

        return services;
    }
}
