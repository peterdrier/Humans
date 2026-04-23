using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Notifications;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Repositories;

namespace Humans.Web.Extensions.Sections;

internal static class NotificationsSectionExtensions
{
    internal static IServiceCollection AddNotificationsSection(this IServiceCollection services)
    {
        // Notifications section — §15 repository pattern (issue #550).
        // No caching decorator: in-app notification dispatch is fire-and-forget
        // and reads go through the inbox service whose nav-badge counts are
        // already cached at the view-component layer via short-TTL IMemoryCache.
        // INotificationRepository is Singleton (IDbContextFactory-based) so the
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
        services.AddScoped<INotificationService, NotificationService>();

        services.AddScoped<NotificationInboxService>();
        services.AddScoped<INotificationInboxService>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<NotificationInboxService>());

        services.AddScoped<INotificationMeterProvider, NotificationMeterProvider>();

        services.AddScoped<CleanupNotificationsJob>();

        return services;
    }
}
