using Humans.Base.Enums;
using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>
/// Contributed to <c>UserPartSlots.ProfileSidebar</c> / <c>AdminDetailSidebar</c> (own-profile
/// and admin hosts only) — renders nothing for <see cref="ProfileCardViewMode.Public"/>, since
/// it has no per-viewer visibility check of its own and would otherwise leak any member's
/// ticket holdings to any authenticated viewer.
/// </summary>
public sealed class TicketHoldingsViewComponent(
    ITicketServiceRead queryService,
    IEarlyEntryService earlyEntryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, ProfileCardViewMode viewMode)
    {
        if (viewMode == ProfileCardViewMode.Public)
            return Content(string.Empty);

        var showEmpty = viewMode == ProfileCardViewMode.Admin;
        var holdings = await queryService.GetUserTicketHoldingsAsync(userId);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.Tickets.Count == 0)
            return Content(string.Empty);

        var earlyEntry = await earlyEntryService.GetForUserAsync(userId, HttpContext.RequestAborted);

        var stubs = holdings.Tickets
            .Select(t => TicketStubInfo.From(t, earlyEntry?.EarliestEntryDate))
            .ToList();

        return View(new TicketHoldingsViewModel(holdings.OrderCount, stubs));
    }
}

internal sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<TicketStubInfo> Tickets);
