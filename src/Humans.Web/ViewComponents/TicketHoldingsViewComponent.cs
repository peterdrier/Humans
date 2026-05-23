using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class TicketHoldingsViewComponent(
    ITicketQueryService queryService,
    ITicketTransferService transferService) : ViewComponent
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

        var stubs = holdings.Tickets
            .Select(t => new TicketStubInfo(
                t.AttendeeName,
                t.AttendeeEmail,
                t.VendorTicketId,
                t.Status,
                pendingAttendeeIds.Contains(t.AttendeeId),
                PendingTransferRequestId: null))
            .ToList();

        return View(new TicketHoldingsViewModel(holdings.OrderCount, stubs));
    }
}

public sealed record TicketHoldingsViewModel(int OrderCount, IReadOnlyList<TicketStubInfo> Tickets);
