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

    public TicketTransferController(
        ITicketTransferService service,
        UserManager<User> userManager)
        : base(userManager)
    {
        _service = service;
    }

    [HttpGet("Request")]
    [ActionName("Request")]
    public IActionResult RequestTransfer(Guid attendeeId)
    {
        var vm = new TicketTransferRequestPageViewModel { AttendeeId = attendeeId };
        return View("Request", vm);
    }

    [HttpPost("Lookup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Lookup(
        Guid attendeeId, string query, CancellationToken ct)
    {
        var (errorResult, user) = await RequireCurrentUserAsync();
        if (errorResult is not null) return errorResult;

        var match = await _service.LookupRecipientAsync(query, user.Id, ct);
        var vm = new TicketTransferRequestPageViewModel
        {
            AttendeeId = attendeeId,
            Query = query,
            Recipient = match is null ? null : new RecipientCardViewModel
            {
                UserId = match.UserId,
                DisplayName = match.DisplayName,
                BurnerName = match.BurnerName,
                PreferredEmail = match.PreferredEmail,
                HasCustomProfilePicture = match.HasCustomProfilePicture,
                ProfilePictureUrl = match.ProfilePictureUrl,
            },
            LookupError = match is null
                ? "No unique match. Try a full email address or a more specific burner name."
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
            SetError(ex.Message);
            return RedirectToAction("Request", new { attendeeId = form.AttendeeId });
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
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(HomeController.Index), "Home");
    }
}
