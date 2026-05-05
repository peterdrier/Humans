using Humans.Application.DTOs;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Entities;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

    [HttpGet("Request")]
    public IActionResult RequestTransfer(Guid attendeeId)
    {
        var vm = new TicketTransferRequestPageViewModel { AttendeeId = attendeeId };
        return View("Request", vm);
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
        IReadOnlyList<RecipientLookupResultDto> matches;
        if (selectedUserId is { } pickedId)
        {
            var card = await _service.GetRecipientCardAsync(pickedId, user.Id, ct);
            matches = card is null
                ? Array.Empty<RecipientLookupResultDto>()
                : new[] { card };
        }
        else
        {
            matches = await _service.LookupRecipientsAsync(query, user.Id, ct);
        }

        var vm = new TicketTransferRequestPageViewModel
        {
            AttendeeId = attendeeId,
            Query = query,
            Recipients = matches.Select(m => new RecipientCardViewModel
            {
                UserId = m.UserId,
                DisplayName = m.DisplayName,
                BurnerName = m.BurnerName,
                PreferredEmail = m.PreferredEmail,
                HasCustomProfilePicture = m.HasCustomProfilePicture,
                ProfilePictureUrl = m.ProfilePictureUrl,
            }).ToList(),
            LookupError = matches.Count == 0
                ? "No match. Try a full email address or a different burner name."
                : null,
        };
        return View("Request", vm);
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
                new TicketTransferRequestDto(form.AttendeeId, form.RecipientUserId, form.Reason),
                user.Id, ct);
            SetSuccess("Transfer requested. A ticket admin will review it shortly.");
            return RedirectToAction(nameof(HomeController.Index), "Home");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Ticket transfer Submit rejected for attendee {AttendeeId}: {Message}",
                form.AttendeeId, ex.Message);
            SetError(ex.Message);
            return RedirectToAction(nameof(RequestTransfer), new { attendeeId = form.AttendeeId });
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
}
