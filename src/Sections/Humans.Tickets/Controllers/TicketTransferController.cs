using Humans.EarlyEntry.Contracts;
using Humans.Tickets.Services;
using Humans.Base.Controllers;
using Humans.Tickets.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Tickets.Services.Dtos;
using Humans.Users.Contracts;

namespace Humans.Tickets.Controllers;

[Authorize]
[Route("Tickets/Transfers")]
internal sealed class TicketTransferController(
    ITicketTransferService service,
    IEarlyEntryService earlyEntryService,
    IUserServiceRead userService,
    ILogger<TicketTransferController> logger,
    IStringLocalizer<TicketsResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync(ct);
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
                ? localizer["Tickets_TicketTransfer_InvalidSelection"].Value
                : null,
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid attendeeId, Guid receiverUserId, string? reason, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var result = await service.CreateRequestAsync(
            new TicketTransferRequestDto(attendeeId, receiverUserId, reason ?? string.Empty), user.Id, ct);
        if (result.Succeeded)
        {
            SetSuccess(localizer["Tickets_TicketTransfer_RequestSubmitted"].Value);
            return RedirectToAction("Index", "Home");
        }

        logger.LogWarning("Ticket transfer Submit rejected for attendee {AttendeeId}: {RefusalKey}",
            attendeeId, result.RefusalKey!);
        var error = localizer[result.RefusalKey!];
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
            Error = error.ResourceNotFound
                ? localizer["Tickets_TicketTransfer_InvalidSelection"].Value
                : error.Value,
        });
    }

    [HttpPost("Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var result = await service.CancelAsync(id, user.Id, ct);
        if (result.Succeeded)
        {
            SetSuccess(localizer["Tickets_TicketTransfer_Cancelled"].Value);
        }
        else
        {
            logger.LogWarning("Ticket transfer Cancel rejected for transfer {TransferId}: {RefusalKey}",
                id, result.RefusalKey);
            var error = localizer[result.RefusalKey!];
            SetError(error.ResourceNotFound
                ? localizer["Tickets_TicketTransfer_CancelFailed"].Value : error.Value);
        }
        return RedirectToAction(nameof(Index));
    }
}
