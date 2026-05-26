using Humans.Application.DTOs;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Tickets/Transfers")]
public sealed class TicketTransferController(
    ITicketTransferService service,
    IEarlyEntryService earlyEntryService,
    IUserServiceRead userService,
    ILogger<TicketTransferController> logger) : HumansControllerBase(userService)
{
    // Step A + B of the wizard: pick a ticket, pick a recipient.
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

    // Step C: resolve the chosen (ticket, recipient) pair server-side and show the confirmation.
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

    // Final submit.
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
            return RedirectToAction(nameof(HomeController.Index), "Home");
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
