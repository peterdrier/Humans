using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// Tickets bought through Humans on a profile. Renders nothing for <see cref="ProfileCardViewMode.Public"/>: it has
/// no viewer check of its own and would show any member's tickets.
/// </summary>
public sealed class IssuedTicketsViewComponent(ISettingsService settings) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode == ProfileCardViewMode.Public)
            return Content(string.Empty);

        // Resolved per call: the checkout service is internal, and a public view component cannot name it in its
        // constructor.
        var checkout = HttpContext.RequestServices.GetRequiredService<Services.Checkout.ITicketCheckoutService>();
        var ct = HttpContext.RequestAborted;
        var tickets = await checkout.GetTicketsForHolderAsync(userId, ct);
        if (tickets.Count == 0)
            return Content(string.Empty);

        var events = (await settings.GetAllEventSettingsAsync(ct)).ToDictionary(e => e.Id, e => e.EventName);
        var eventNames = (await checkout.GetSalesEventsAsync(ct))
            .ToDictionary(s => s.Id, s => events.GetValueOrDefault(s.EventSettingsId, string.Empty));
        var rows = tickets
            .OrderByDescending(t => t.IssuedAt)
            .Select(t => new IssuedTicketRow(
                eventNames.GetValueOrDefault(t.SalesEventId, string.Empty), t.Kind.ToString(), t.State.ToString(), t.Barcode))
            .ToList();
        return View(new IssuedTicketsViewModel(viewMode, rows));
    }
}

internal sealed record IssuedTicketRow(string EventName, string Kind, string State, string Barcode);

internal sealed record IssuedTicketsViewModel(ProfileCardViewMode ViewMode, IReadOnlyList<IssuedTicketRow> Tickets);
