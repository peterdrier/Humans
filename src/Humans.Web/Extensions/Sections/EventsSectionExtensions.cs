using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Events;
using Humans.Infrastructure.Repositories.Events;
using Humans.Web.Filters;

namespace Humans.Web.Extensions.Sections;

internal static class EventsSectionExtensions
{
    internal static IServiceCollection AddEventsSection(this IServiceCollection services)
    {
        services.AddScoped<EventsFeatureFilter>();
        // Singleton + IDbContextFactory pattern (§15b): repo owns context lifetime.
        services.AddSingleton<IEventRepository, EventRepository>();
        // Single EventService instance forwards to IEventService + IUserDataContributor.
        services.AddScoped<EventService>();
        services.AddScoped<IEventService>(sp => sp.GetRequiredService<EventService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<EventService>());
        return services;
    }
}
