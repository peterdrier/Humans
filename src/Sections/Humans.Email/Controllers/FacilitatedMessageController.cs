using Humans.AuditLog.Contracts;
using Humans.Base;
using Humans.Base.Controllers;
using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Controllers;

/// <summary>
/// Facilitated messaging: one member sends another a one-off email relayed through Humans, at
/// <c>/Profile/{id}/SendMessage</c>. A conduit, not a mailbox: nothing is stored beyond the
/// outbox row and the audit entry, and the recipient's address is never shown to the sender.
/// </summary>
/// <remarks>
/// The route keeps the <c>Profile</c> prefix so the URL members already have does not change,
/// as <c>MemberOutboxController</c> does. The links that lead here are Users' profile-card
/// button and Email's <c>TeamMessageButtonsViewComponent</c> user part.
/// </remarks>
[Authorize]
internal sealed class FacilitatedMessageController(
    IUserService userService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    ICommunicationPreferenceService commPrefService,
    IAuditLogService auditLogService,
    ITeamMessageOptionsProvider teamMessageOptionsProvider,
    IStringLocalizer<EmailResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : HumansControllerBase(userService)
{
    private const string ProfileController = "ProfileView";
    private const string ViewProfileAction = "ViewProfile";

    [HttpGet("Profile/{id:guid}/SendMessage")]
    public async Task<IActionResult> SendMessage(Guid id, Guid? teamId, CancellationToken ct)
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

        var teamSender = teamId is null
            ? null
            : (await teamMessageOptionsProvider.GetOptionsAsync(currentUser.Id, ct))
                .FirstOrDefault(t => t.TeamId == teamId.Value);
        if (teamId is not null && teamSender is null)
            return Forbid();

        var viewModel = new SendMessageViewModel
        {
            RecipientId = id,
            RecipientDisplayName = targetInfo.BurnerName,
            SenderEmail = currentUser.Email ?? string.Empty,
            SendAsTeamId = teamSender?.TeamId,
            SendAsTeamName = teamSender?.TeamName,
            TeamReplyToEmail = teamSender?.GoogleGroupEmail,
        };

        return View(viewModel);
    }

    [HttpPost("Profile/{id:guid}/SendMessage")]
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

        var teamSender = model.SendAsTeamId is null
            ? null
            : (await teamMessageOptionsProvider.GetOptionsAsync(currentUser.Id, ct))
                .FirstOrDefault(t => t.TeamId == model.SendAsTeamId.Value);
        if (model.SendAsTeamId is not null && teamSender is null)
            return Forbid();

        model.SendAsTeamName = teamSender?.TeamName;
        model.TeamReplyToEmail = teamSender?.GoogleGroupEmail;

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

        var message = WithTeamReplyTo(emailMessages.FacilitatedMessage(
            request.RecipientEmail,
            request.RecipientDisplayName,
            request.SenderDisplayName,
            request.Message,
            request.IncludeContactInfo,
            request.SenderEmail,
            request.RecipientPreferredLanguage), teamSender);

        await emailService.SendAsync(message, CancellationToken.None);

        await auditLogService.LogAsync(
            AuditAction.FacilitatedMessageSent,
            "User", targetUser.Id,
            DescribeFacilitatedMessage(targetUser.BurnerName, model.IncludeContactInfo, teamSender),
            currentUser.Id,
            relatedEntityId: teamSender?.TeamId,
            relatedEntityType: teamSender is null ? null : "Team");

        SetSuccess(string.Format(
            localizer["Email_SendMessage_Success"].Value,
            targetUser.BurnerName));

        return RedirectToProfile(id);
    }

    private IActionResult RedirectToProfile(Guid id) =>
        RedirectToAction(ViewProfileAction, ProfileController, new { id });

    private static EmailMessage WithTeamReplyTo(EmailMessage message, TeamMessageOption? teamSender) =>
        teamSender is null ? message : message with { ReplyTo = teamSender.GoogleGroupEmail };

    private static string DescribeFacilitatedMessage(
        string recipientName,
        bool includeContactInfo,
        TeamMessageOption? teamSender)
    {
        var contactInfo = includeContactInfo ? "yes" : "no";
        return teamSender is null
            ? $"Message sent to {recipientName} (contact info shared: {contactInfo})"
            : $"Message sent to {recipientName} from team {teamSender.TeamName} (contact info shared: {contactInfo})";
    }
}
