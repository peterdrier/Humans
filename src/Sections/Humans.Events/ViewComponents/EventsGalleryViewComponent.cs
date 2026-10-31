using Humans.Events.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Humans.Events.ViewComponents;

/// <summary>
/// The widget gallery's Events samples. The search-result row renders nothing for an unresolved key, so it is keyed
/// off the first approved event; null when there is none or <c>Features:Events</c> is off, so the gallery never
/// becomes a second producer of event ids behind the flag's back.
/// </summary>
internal sealed class EventsGalleryViewComponent(IEventService eventService, IConfiguration configuration) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        Guid? eventId = null;
        if (configuration.GetValue<bool>("Features:Events"))
        {
            var events = await eventService.GetApprovedEventsAsync(
                campId: null, venueId: null, categoryId: null, q: null, excludedSlugs: [], HttpContext.RequestAborted);
            eventId = events.MinBy(e => e.StartAt)?.Id;
        }

        return View(new EventsGalleryViewModel(eventId));
    }
}

internal sealed record EventsGalleryViewModel(Guid? EventId);
