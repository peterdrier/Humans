using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;

namespace Humans.Web.Extensions.Sections;

internal static class NotificationsSectionExtensions
{
    internal static IServiceCollection AddNotificationsSection(this IServiceCollection services)
    {
        services.AddScoped<INotificationService, NotificationService>();

        services.AddScoped<NotificationInboxService>();
        services.AddScoped<INotificationInboxService>(sp => sp.GetRequiredService<NotificationInboxService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<NotificationInboxService>());

        services.AddScoped<INotificationMeterProvider, NotificationMeterProvider>();

        services.AddScoped<CleanupNotificationsJob>();

        return services;
    }
}
