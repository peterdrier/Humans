using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Humans.Notifications.Contracts;
using Humans.Notifications.Data;
using Humans.Notifications.Jobs;
using Humans.Notifications.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;

namespace Humans.Notifications;

/// <summary>
/// Notifications' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix. No caching decorator (issue #550): the
/// inbox service owns the per-user badge-count cache and evicts it on every write, and the
/// meter provider owns the meter-aggregate cache.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<NotificationsDbContext>(sentinelTable: "notifications");

        // §15 repository pattern (issue #550). Singleton + IDbContextFactory so the
        // services can inject it directly.
        services.AddSingleton<INotificationRepository, NotificationRepository>();

        // DI cycle break: NotificationEmitter is a distinct type from
        // NotificationService. TeamService and RoleAssignmentService depend on
        // INotificationEmitter (= NotificationEmitter, no resolver dependency),
        // which does NOT inject INotificationRecipientResolver. The resolver
        // (which depends on ITeamService + IRoleAssignmentService) is only
        // pulled in by NotificationService, so no edge closes the cycle.
        services.AddScoped<INotificationEmitter, NotificationEmitter>();
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
        services.AddScoped<NotificationService>();
        services.AddScoped<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<NotificationService>());

        services.AddScoped<NotificationInboxService>();
        services.AddScoped<INotificationInboxService>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<INotificationAutoResolve>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<INotificationRetention>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<NotificationInboxService>());

        services.AddScoped<NotificationMeterProvider>();

        services.AddScoped<CleanupNotificationsJob>();
    }
}
