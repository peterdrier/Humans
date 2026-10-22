using Humans.AuditLog.Contracts;
using Humans.Base;
using Humans.Base.Controllers;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Teams.Models;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Teams.Controllers;

/// <summary>
/// The team is authorized through <see cref="ITeamMessageOptionsProvider"/>; Email relays the message with the group as
/// reply-to and keeps no team logic.
/// </summary>
[Authorize]
[Route("Teams")]
internal sealed class TeamMessageController(
    IUserService userService,
    ITeamMessageOptionsProvider teamOptions,
    ICommunicationPreferenceService commPrefs,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    IAuditLogService auditLogService,
    IStringLocalizer<TeamsResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer) : HumansControllerBase(userService)
{
    private const string ProfileController = "ProfileView";
    private const string ViewProfileAction = "ViewProfile";

    [HttpGet("{teamId:guid}/SendMessage/{id:guid}")]
    public async Task<IActionResult> SendMessage(Guid teamId, Guid id, CancellationToken ct)
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

        if (!await commPrefs.AcceptsFacilitatedMessagesAsync(id, ct))
        {
            SetError(localizer["Teams_SendMessage_OptedOut"].Value);
            return RedirectToProfile(id);
        }

        var team = await FindTeamAsync(currentUser.Id, teamId, ct);
        if (team is null)
            return Forbid();

        return View(new TeamSendMessageViewModel
        {
            TeamId = team.TeamId,
            TeamName = team.TeamName,
            TeamReplyToEmail = team.GoogleGroupEmail,
            RecipientId = id,
            RecipientDisplayName = targetInfo.BurnerName,
            SenderEmail = currentUser.Email ?? string.Empty,
        });
    }

    [HttpPost("{teamId:guid}/SendMessage/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(
        Guid teamId, Guid id, TeamSendMessageViewModel model, CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync(ct);
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToProfile(id);

        var participants = await UserService.GetUserInfosAsync([id, currentUser.Id], ct);
        if (!participants.TryGetValue(id, out var targetUser))
            return NotFound();
        id = targetUser.Id;
        if (currentUser.Id == id)
            return RedirectToProfile(id);

        if (!await commPrefs.AcceptsFacilitatedMessagesAsync(id, ct))
        {
            SetError(localizer["Teams_SendMessage_OptedOut"].Value);
            return RedirectToProfile(id);
        }

        var team = await FindTeamAsync(currentUser.Id, teamId, ct);
        if (team is null)
            return Forbid();

        model.TeamId = team.TeamId;
        model.TeamName = team.TeamName;
        model.TeamReplyToEmail = team.GoogleGroupEmail;
        model.RecipientId = id;
        model.RecipientDisplayName = targetUser.BurnerName;
        model.SenderEmail = currentUser.Email ?? string.Empty;

        if (!ModelState.IsValid)
            return View(model);

        if (!participants.TryGetValue(currentUser.Id, out var sender))
            return NotFound();

        if (string.IsNullOrWhiteSpace(targetUser.Email) || string.IsNullOrWhiteSpace(sender.Email))
        {
            ModelState.AddModelError(string.Empty, sharedLocalizer["Common_Error"].Value);
            return View(model);
        }

        await emailService.SendAsync(emailMessages.FacilitatedMessage(
            targetUser.Email,
            targetUser.BurnerName,
            sender.BurnerName,
            model.Message,
            model.IncludeContactInfo,
            sender.Email,
            targetUser.PreferredLanguage.IsSupportedCultureCode()
                ? targetUser.PreferredLanguage
                : CultureCatalog.DefaultCultureCode,
            replyTo: team.GoogleGroupEmail), CancellationToken.None);

        // Name and link the team; never copy its address into the audit text.
        var contactInfo = model.IncludeContactInfo ? "yes" : "no";
        await auditLogService.LogAsync(
            AuditAction.FacilitatedMessageSent,
            "User", targetUser.Id,
            $"Message sent to {targetUser.BurnerName} from team {team.TeamName} (contact info shared: {contactInfo})",
            currentUser.Id,
            relatedEntityId: team.TeamId,
            relatedEntityType: "Team");

        SetSuccess(string.Format(localizer["Teams_SendMessage_Success"].Value, targetUser.BurnerName));

        return RedirectToProfile(id);
    }

    private async Task<TeamMessageOption?> FindTeamAsync(Guid viewerId, Guid teamId, CancellationToken ct) =>
        (await teamOptions.GetOptionsAsync(viewerId, ct)).FirstOrDefault(t => t.TeamId == teamId);

    private IActionResult RedirectToProfile(Guid id) =>
        RedirectToAction(ViewProfileAction, ProfileController, new { id });
}
