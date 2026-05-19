using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Tickets/Transfers")]
public sealed class TicketTransferController(
    ITicketTransferService service,
    IUserService userService,
    ILogger<TicketTransferController> logger) : HumansControllerBase(userService)
{
    [HttpGet("Send")]
    public IActionResult Send(Guid attendeeId)
    {
        var vm = new TicketTransferRequestPageViewModel { AttendeeId = attendeeId };
        return View("Send", vm);
    }

    [HttpPost("Lookup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Lookup(
        Guid attendeeId, string query, Guid? selectedUserId, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        // Free-text query OR selectedUserId (direct resolve, skips search).
        IReadOnlyList<ReceiverLookupResultDto> matches;
        if (selectedUserId is { } pickedId)
        {
            var card = await service.GetReceiverCardAsync(pickedId, user.Id, ct);
            matches = card is null
                ? Array.Empty<ReceiverLookupResultDto>()
                : new[] { card };
        }
        else
        {
            matches = await service.LookupReceiversAsync(query, user.Id, ct);
        }

        var vm = BuildPageViewModel(attendeeId, query, matches);
        vm.LookupError = matches.Count == 0
            ? "No match. Try a full email address or a different burner name."
            : null;
        return View("Send", vm);
    }

    [HttpPost("Submit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(
        TicketTransferConfirmFormViewModel form, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        try
        {
            await service.CreateRequestAsync(
                new TicketTransferRequestDto(form.AttendeeId, form.ReceiverUserId, form.Reason),
                user.Id, ct);
            SetSuccess("Transfer requested. A ticket admin will review it shortly.");
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Ticket transfer Submit rejected for attendee {AttendeeId}: {Message}",
                form.AttendeeId, ex.Message);
            SetError(ex.Message);

            // Preserve Receiver + reason on re-render.
            var card = await service.GetReceiverCardAsync(form.ReceiverUserId, user.Id, ct);
            var matches = card is null
                ? Array.Empty<ReceiverLookupResultDto>()
                : new[] { card };
            var vm = BuildPageViewModel(form.AttendeeId, query: null, matches);
            vm.PrefilledReason = form.Reason;
            return View("Send", vm);
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
        return RedirectToAction(nameof(HomeController.Index), "Home");
    }

    private static TicketTransferRequestPageViewModel BuildPageViewModel(
        Guid attendeeId, string? query, IReadOnlyList<ReceiverLookupResultDto> matches) =>
        new()
        {
            AttendeeId = attendeeId,
            Query = query,
            Receivers = matches.Select(m => new ReceiverCardViewModel
            {
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                BurnerName = m.BurnerName,
                PreferredEmail = m.PreferredEmail,
                HasCustomProfilePicture = m.HasCustomProfilePicture,
                ProfilePictureUrl = m.ProfilePictureUrl,
            }).ToList(),
        };
}
