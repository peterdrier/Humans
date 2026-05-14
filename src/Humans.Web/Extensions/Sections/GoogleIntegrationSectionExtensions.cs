using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.GoogleIntegration;
using GoogleSyncSettingsService = Humans.Application.Services.GoogleIntegration.SyncSettingsService;
using GoogleEmailProvisioningService = Humans.Application.Services.GoogleIntegration.EmailProvisioningService;
using GoogleAdminService = Humans.Application.Services.GoogleIntegration.GoogleAdminService;
using GoogleDriveActivityMonitorService = Humans.Application.Services.GoogleIntegration.DriveActivityMonitorService;
using GoogleRemovalNotificationService = Humans.Application.Services.GoogleIntegration.GoogleRemovalNotificationService;

namespace Humans.Web.Extensions.Sections;

internal static class GoogleIntegrationSectionExtensions
{
    internal static IServiceCollection AddGoogleIntegrationSection(this IServiceCollection services)
    {
        // Sync settings — repository is Singleton (IDbContextFactory-based per §15b);
        // service is Scoped and lives in Humans.Application.
        // Previously registered in AdminSectionExtensions (issue #554 §15 migration).
        services.AddSingleton<ISyncSettingsRepository, SyncSettingsRepository>();
        services.AddScoped<ISyncSettingsService, GoogleSyncSettingsService>();

        // Email provisioning — Application-layer service; depends on IUserService /
        // IProfileService / IUserEmailService / IGoogleWorkspaceUserService.
        // Previously registered in ProfileSectionExtensions (issue #554 §15 migration).
        services.AddScoped<IEmailProvisioningService, GoogleEmailProvisioningService>();

        // Team-resource repository — Singleton (IDbContextFactory-based per §15b).
        services.AddSingleton<IGoogleResourceRepository, GoogleResourceRepository>();

        // Google sync outbox repository — Singleton via IDbContextFactory per §15b.
        // Exposes narrow count queries so Notifications / Metrics / Admin-digest
        // consumers don't read google_sync_outbox_events directly.
        services.AddSingleton<IGoogleSyncOutboxRepository, GoogleSyncOutboxRepository>();

        // Drive activity monitor — repository is Singleton (IDbContextFactory-based);
        // service is Application-layer and depends only on IGoogleDriveActivityClient
        // and the repository.
        services.AddSingleton<IDriveActivityMonitorRepository, DriveActivityMonitorRepository>();
        services.AddScoped<IDriveActivityMonitorService, GoogleDriveActivityMonitorService>();

        // Google admin service — Application-layer; Scoped.
        services.AddScoped<IGoogleAdminService, GoogleAdminService>();

        // Removal notification service — Application-layer; depends only on
        // IUserService / IUserEmailService / IEmailService. Registered unconditionally
        // because the no-op stub sync service does not call it.
        services.AddScoped<IGoogleRemovalNotificationService, GoogleRemovalNotificationService>();

        return services;
    }
}
