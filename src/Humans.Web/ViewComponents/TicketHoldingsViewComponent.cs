using Humans.Application.DTOs;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketHoldingsViewComponent(
    ITicketServiceRead queryService,
    ITicketTransferService transferService,
    IEarlyEntryService earlyEntryService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, bool showEmpty = false)
    {
        var holdings = await queryService.GetUserTicketHoldingsAsync(userId);

        if (!showEmpty && holdings.OrderCount == 0 && holdings.Tickets.Count == 0)
            return Content(string.Empty);

        // Pending outgoing transfers so the stub can show the "transfer pending" stamp.
        var pendingAttendeeIds = (await transferService.GetMyAttendeesAsync(userId))
            .Where(a => a.HasPendingOutgoingTransfer)
            .Select(a => a.AttendeeId)
            .ToHashSet();

        // The holder's own EE (earliest date across sources), shown on each of their stubs.
        var earlyEntry = await earlyEntryService.GetForUserAsync(userId, HttpContext.RequestAborted);

        var stubs = holdings.Tickets
            .Select(t => new TicketStubInfo(
                t.AttendeeName,
                t.AttendeeEmail,
                t.VendorTicketId,
                t.Status,
                pendingAttendeeIds.Contains(t.AttendeeId),
                PendingTransferRequestId: null,
                EarlyEntryDate: earlyEntry?.EarliestEntryDate))
            .ToList();

        return View(new TicketHoldingsViewModel(holdings.OrderCount, stubs));
    }
}

public sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<TicketStubInfo> Tickets);
