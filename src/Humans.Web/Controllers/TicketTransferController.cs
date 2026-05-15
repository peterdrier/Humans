using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Tickets/Transfers")]
public sealed class TicketTransferController : HumansControllerBase
{
    private readonly ITicketTransferService _service;
    private readonly ILogger<TicketTransferController> _logger;

    public TicketTransferController(
        ITicketTransferService service,
        UserManager<User> userManager,
        ILogger<TicketTransferController> logger)
        : base(userManager)
    {
        _service = service;
        _logger = logger;
    }

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

        // Two flows hit this action:
        //  1. Free-text query → service returns 0..N candidates
        //  2. selectedUserId set → user picked one from a multi-match list,
        //     resolve that single id directly so we render a single card
        //     (skips re-running the search).
        IReadOnlyList<ReceiverLookupResultDto> matches;
        if (selectedUserId is { } pickedId)
        {
            var card = await _service.GetReceiverCardAsync(pickedId, user.Id, ct);
            matches = card is null
                ? Array.Empty<ReceiverLookupResultDto>()
                : new[] { card };
        }
        else
        {
            matches = await _service.LookupReceiversAsync(query, user.Id, ct);
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
            await _service.CreateRequestAsync(
                new TicketTransferRequestDto(form.AttendeeId, form.ReceiverUserId, form.Reason),
                user.Id, ct);
            SetSuccess("Transfer requested. A ticket admin will review it shortly.");
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Ticket transfer Submit rejected for attendee {AttendeeId}: {Message}",
                form.AttendeeId, ex.Message);
            SetError(ex.Message);

            // Re-render the confirm form with the Receiver pick + reason intact
            // so the Sender doesn't have to redo the lookup or retype.
            var card = await _service.GetReceiverCardAsync(form.ReceiverUserId, user.Id, ct);
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
            await _service.CancelAsync(id, user.Id, ct);
            SetSuccess("Transfer cancelled.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Ticket transfer Cancel rejected for transfer {TransferId}: {Message}",
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
