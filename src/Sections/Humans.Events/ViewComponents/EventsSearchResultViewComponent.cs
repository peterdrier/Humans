using Humans.Events.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Events.ViewComponents;

/// <summary>
/// Public: Razor's discovery only sees public view components; an internal one ships as inert markup. No
/// <c>Features:Events</c> check of its own: callers gate on the flag.
/// </summary>
public sealed class EventsSearchResultViewComponent(IEventService events) : ViewComponent
{
    /// <summary>Nothing when the event no longer resolves as approved.</summary>
    public async Task<IViewComponentResult> InvokeAsync(Guid eventId)
    {
        // O(1) on the approved-only cache: no query, and no per-row scan either.
        var match = await events.GetApprovedEventByIdAsync(eventId, HttpContext.RequestAborted);
        return match is null
            ? Content(string.Empty)
            : View(new EventsSearchResultViewModel(match.Title, match.CategoryName));
    }
}

internal sealed record EventsSearchResultViewModel(string Title, string CategoryName);
