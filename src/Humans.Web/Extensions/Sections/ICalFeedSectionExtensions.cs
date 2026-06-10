using Humans.Application.Interfaces.ICalFeed;
using Humans.Application.Services.ICalFeed;

namespace Humans.Web.Extensions.Sections;

internal static class ICalFeedSectionExtensions
{
    internal static IServiceCollection AddICalFeedSection(this IServiceCollection services)
    {
        // iCal feed orchestrator — pure fan-out over every ICalendarFeedContributor
        services.AddScoped<IICalFeedService, ICalFeedService>();

        return services;
    }
}
