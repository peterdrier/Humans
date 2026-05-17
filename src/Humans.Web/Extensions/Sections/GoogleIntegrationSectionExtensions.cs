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
        services.AddSingleton<ISyncSettingsRepository, SyncSettingsRepository>();
        services.AddScoped<ISyncSettingsService, GoogleSyncSettingsService>();
        services.AddScoped<IEmailProvisioningService, GoogleEmailProvisioningService>();
        services.AddSingleton<IGoogleResourceRepository, GoogleResourceRepository>();
        services.AddSingleton<IGoogleSyncOutboxRepository, GoogleSyncOutboxRepository>();
        services.AddSingleton<IDriveActivityMonitorRepository, DriveActivityMonitorRepository>();
        services.AddScoped<IDriveActivityMonitorService, GoogleDriveActivityMonitorService>();
        services.AddScoped<IGoogleAdminService, GoogleAdminService>();
        services.AddScoped<IGoogleRemovalNotificationService, GoogleRemovalNotificationService>();

        return services;
    }
}
