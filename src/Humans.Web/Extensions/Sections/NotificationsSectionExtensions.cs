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

        // Resolver breaks the circular DI graph between INotificationService and
        // ITeamService/IRoleAssignmentService (which inject INotificationService
        // back). Only NotificationService depends on the resolver.
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();

        // Register NotificationService under both INotificationService and the
        // narrower INotificationEmitter. TeamService and RoleAssignmentService
        // depend on INotificationEmitter to avoid the cycle through
        // INotificationRecipientResolver (which transitively injects them).
        services.AddScoped<NotificationService>();
        services.AddScoped<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddScoped<INotificationEmitter>(sp => sp.GetRequiredService<NotificationService>());

        services.AddScoped<NotificationInboxService>();
        services.AddScoped<INotificationInboxService>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<NotificationInboxService>());

        services.AddScoped<INotificationMeterProvider, NotificationMeterProvider>();

        services.AddScoped<CleanupNotificationsJob>();

        return services;
    }
}
