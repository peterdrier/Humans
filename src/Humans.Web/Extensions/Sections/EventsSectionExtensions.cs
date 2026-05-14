using Humans.Application.Interfaces.Events;
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
        services.AddScoped<IEventService, EventService>();
        return services;
    }
}
