using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Notifications.Contracts;
using Humans.Notifications.Data;
using Humans.Notifications.Jobs;
using Humans.Notifications.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Humans.Users.Contracts;

namespace Humans.Notifications;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<NotificationsDbContext>(sentinelTable: "notifications");

        services.AddSingleton<INotificationRepository, NotificationRepository>();

        services.AddSingleton<NotificationSourceRegistry>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NotificationSourceRegistry>());

        // A separate type from NotificationService so TeamService and RoleAssignmentService can take
        // INotificationEmitter without a DI cycle. Only ValidateOnBuild at startup catches a regression.
        services.AddScoped<INotificationEmitter, NotificationEmitter>();
        services.AddScoped<NotificationService>();
        services.AddScoped<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<NotificationService>());

        services.AddScoped<NotificationInboxService>();
        services.AddScoped<INotificationInboxService>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<INotificationAutoResolve>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<INotificationRetention>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<NotificationInboxService>());

        services.AddScoped<NotificationMeterProvider>();
        services.AddScoped<INotificationInboxRead, NotificationInboxRead>();

        services.AddScoped<CleanupNotificationsJob>();
    }
}
