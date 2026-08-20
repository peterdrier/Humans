using Humans.Events.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Events.ViewComponents;

/// <summary>
/// One event-search result row, keyed by event id
/// (nobodies-collective/Humans#1062). Callers hold the id their own search
/// produced; Events owns what an event row looks like and the Browse link it
/// points at. Replaces the projection <c>SearchService</c> used to build.
/// </summary>
/// <remarks>
/// Public because Razor's compile-time discovery filters on public — an internal
/// view component ships <c>&lt;vc:…&gt;</c> as inert markup on a green build
/// (HUM0034's framework exception). No <c>Features:Events</c> check of its own —
/// callers gate on the flag: an off flag empties the search bucket, and the widget
/// gallery skips the card outright.
/// </remarks>
public sealed class EventsSearchResultViewComponent(IEventServiceRead events) : ViewComponent
{
    /// <param name="eventId">The matched approved event.</param>
    public async Task<IViewComponentResult> InvokeAsync(Guid eventId)
    {
        // Cache-served, so a bucket of rows costs no query — but it is a scan per row.
        // CachingEventService.GetApprovedEventByIdAsync is the O(1) read; it is not on
        // IEventServiceRead, and a public view component cannot take the internal
        // IEventService (CS0051), so reaching it needs new public surface.
        var approved = await events.GetApprovedEventsAsync(
            campId: null, venueId: null, categoryId: null, q: null, excludedSlugs: []);
        var match = approved.FirstOrDefault(e => e.Id == eventId);
        return match is null
            ? Content(string.Empty)
            : View(new EventsSearchResultViewModel(match.Title, match.CategoryName));
    }
}

internal sealed record EventsSearchResultViewModel(string Title, string CategoryName);
