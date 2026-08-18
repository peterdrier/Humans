using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Services;
using Humans.Base.Controllers;
using Humans.Tickets.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Tickets.Services.Dtos;
using Humans.Users.Contracts;

namespace Humans.Tickets.Controllers;

[Authorize]
[Route("Tickets/Transfers")]
internal sealed class TicketTransferController(
    ITicketTransferService service,
    IEarlyEntryService earlyEntryService,
    IUserServiceRead userService,
    ILogger<TicketTransferController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var mine = await service.GetMyAttendeesAsync(user.Id, ct);
        var transfers = await service.GetBySenderAsync(user.Id, ct);
        var earlyEntry = await earlyEntryService.GetForUserAsync(user.Id, ct);
        return View("Index", new TicketTransferWizardViewModel
        {
            MyTickets = mine,
            MyTransfers = transfers,
            HolderEarlyEntry = earlyEntry?.EarliestEntryDate,
        });
    }

    [HttpPost("Confirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(Guid attendeeId, Guid receiverUserId, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var mine = await service.GetMyAttendeesAsync(user.Id, ct);
        var transfers = await service.GetBySenderAsync(user.Id, ct);
        var confirm = await service.GetConfirmationAsync(attendeeId, receiverUserId, user.Id, ct);
        var earlyEntry = await earlyEntryService.GetForUserAsync(user.Id, ct);
        return View("Index", new TicketTransferWizardViewModel
        {
            MyTickets = mine,
            MyTransfers = transfers,
            HolderEarlyEntry = earlyEntry?.EarliestEntryDate,
            Confirm = confirm,
            Error = confirm is null
                ? "Couldn't set up that transfer — choose one of your tickets and a valid recipient (not yourself)."
                : null,
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid attendeeId, Guid receiverUserId, string? reason, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await service.CreateRequestAsync(
                new TicketTransferRequestDto(attendeeId, receiverUserId, reason ?? string.Empty), user.Id, ct);
            SetSuccess("Transfer requested. Our ticketing team will process it and let you know shortly.");
            return RedirectToAction("Index", "Home");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Ticket transfer Submit rejected for attendee {AttendeeId}: {Message}",
                attendeeId, ex.Message);
            var mine = await service.GetMyAttendeesAsync(user.Id, ct);
            var transfers = await service.GetBySenderAsync(user.Id, ct);
            var confirm = await service.GetConfirmationAsync(attendeeId, receiverUserId, user.Id, ct);
            var earlyEntry = await earlyEntryService.GetForUserAsync(user.Id, ct);
            return View("Index", new TicketTransferWizardViewModel
            {
                MyTickets = mine,
                MyTransfers = transfers,
                HolderEarlyEntry = earlyEntry?.EarliestEntryDate,
                Confirm = confirm,
                Reason = reason,
                Error = ex.Message,
            });
        }
    }

    [HttpPost("Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await service.CancelAsync(id, user.Id, ct);
            SetSuccess("Transfer cancelled.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Ticket transfer Cancel rejected for transfer {TransferId}: {Message}",
                id, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Index));
    }
}
