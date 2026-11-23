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
        var held = await checkout.GetTicketsForHolderAsync(userId, ct);
        // A child ticket has no holder of its own; it belongs on the page of the adult it is attached to.
        var tickets = held.ToList();
        foreach (var orderId in held.Select(t => t.OrderId).Distinct())
            tickets.AddRange((await checkout.GetTicketsForOrderAsync(orderId, ct))
                .Where(c => c.HolderUserId is null && held.Any(a => a.Id == c.ParentTicketId)));
        if (tickets.Count == 0)
            return Content(string.Empty);

        var events = (await settings.GetAllEventSettingsAsync(ct)).ToDictionary(e => e.Id, e => e.EventName);
        var eventNames = (await checkout.ListTicketingSettingsAsync(ct))
            .ToDictionary(s => s.Id, s => events.GetValueOrDefault(s.EventSettingsId, string.Empty));
        var rows = tickets
            .OrderByDescending(t => t.IssuedAt).ThenBy(t => t.ParentTicketId is not null)
            .Select(t => new IssuedTicketRow(
                eventNames.GetValueOrDefault(t.TicketingSettingsId, string.Empty), t.Kind.ToString(), t.State.ToString(), t.Barcode,
                t.HolderUserId is null ? t.HolderName : null))
            .ToList();
        return View(new IssuedTicketsViewModel(viewMode, rows));
    }
}

/// <summary><paramref name="ChildName"/> is set only for a child ticket listed under its adult.</summary>
internal sealed record IssuedTicketRow(string EventName, string Kind, string State, string Barcode, string? ChildName);

internal sealed record IssuedTicketsViewModel(ProfileCardViewMode ViewMode, IReadOnlyList<IssuedTicketRow> Tickets);
