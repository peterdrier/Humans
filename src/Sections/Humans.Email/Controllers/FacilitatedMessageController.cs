using Humans.AuditLog.Contracts;
using Humans.Base;
using Humans.Base.Controllers;
using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Controllers;

/// <summary>
/// Facilitated messaging: one member sends another a one-off email relayed through Humans, at
/// <c>/Email/SendMessage/{id}</c>. A conduit, not a mailbox: nothing is stored beyond the
/// outbox row and the audit entry, and the recipient's address is never shown to the sender.
/// </summary>
/// <remarks>
/// The link that leads here is Users' profile-card button. Sending on behalf of a team is
/// Teams' (<c>/Teams/{teamId}/SendMessage/{id}</c>), through the same
/// <see cref="IEmailMessageFactory.FacilitatedMessage"/> with the team's group as reply-to.
/// </remarks>
[Authorize]
internal sealed class FacilitatedMessageController(
    IUserService userService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    ICommunicationPreferenceService commPrefService,
    IAuditLogService auditLogService,
    IStringLocalizer<EmailResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : HumansControllerBase(userService)
{
    private const string ProfileController = "ProfileView";
    private const string ViewProfileAction = "ViewProfile";

    [HttpGet("Email/SendMessage/{id:guid}")]
    public async Task<IActionResult> SendMessage(Guid id, CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync(ct);
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToProfile(id);

        var targetInfo = await UserService.GetUserInfoAsync(id, ct);
        if (targetInfo is null)
            return NotFound();
        id = targetInfo.Id;
        if (currentUser.Id == id)
            return RedirectToProfile(id);

        if (!await commPrefService.AcceptsFacilitatedMessagesAsync(id, ct))
        {
            SetError(localizer["Email_SendMessage_OptedOut"].Value);
            return RedirectToProfile(id);
        }

        var viewModel = new SendMessageViewModel
        {
            RecipientId = id,
            RecipientDisplayName = targetInfo.BurnerName,
            SenderEmail = currentUser.Email ?? string.Empty,
        };

        return View(viewModel);
    }

    [HttpPost("Email/SendMessage/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid id, SendMessageViewModel model, CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync(ct);
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToProfile(id);

        // Bulk-fetch via section service, not cross-domain nav.
        var participants = await UserService.GetUserInfosAsync([id, currentUser.Id], ct);
        if (!participants.TryGetValue(id, out var targetUser))
            return NotFound();
        id = targetUser.Id;
        if (currentUser.Id == id)
            return RedirectToProfile(id);

        if (!await commPrefService.AcceptsFacilitatedMessagesAsync(id, ct))
        {
            SetError(localizer["Email_SendMessage_OptedOut"].Value);
            return RedirectToProfile(id);
        }

        model.RecipientId = id;
        model.RecipientDisplayName = targetUser.BurnerName;
        model.SenderEmail = currentUser.Email ?? string.Empty;

        if (!ModelState.IsValid)
            return View(model);

        if (!participants.TryGetValue(currentUser.Id, out var sender))
            return NotFound();

        var request = FacilitatedMessageRequestBuilder.TryBuild(sender, targetUser, model);
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, sharedLocalizer["Common_Error"].Value);
            return View(model);
        }

        var message = emailMessages.FacilitatedMessage(
            request.RecipientEmail,
            request.RecipientDisplayName,
            request.SenderDisplayName,
            request.Message,
            request.IncludeContactInfo,
            request.SenderEmail,
            request.RecipientPreferredLanguage);

        await emailService.SendAsync(message, CancellationToken.None);

        await auditLogService.LogAsync(
            AuditAction.FacilitatedMessageSent,
            "User", targetUser.Id,
            $"Message sent to {targetUser.BurnerName} (contact info shared: {(model.IncludeContactInfo ? "yes" : "no")})",
            currentUser.Id);

        SetSuccess(string.Format(
            localizer["Email_SendMessage_Success"].Value,
            targetUser.BurnerName));

        return RedirectToProfile(id);
    }

    private IActionResult RedirectToProfile(Guid id) =>
        RedirectToAction(ViewProfileAction, ProfileController, new { id });
}
