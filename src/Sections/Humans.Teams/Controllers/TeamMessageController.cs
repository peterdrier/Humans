using Humans.Base;
using Humans.Base.Controllers;
using Humans.Teams.Models;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.Teams.Controllers;

/// <summary>The controller authorizes the selected send-as team; the provider owns eligibility and delivery.</summary>
[Authorize]
[Route("Teams")]
internal sealed class TeamMessageController(
    IUserService userService,
    ITeamMessageOptionsProvider teamOptions,
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
        var context = await teamOptions.GetContextAsync(currentUser.Id, id, ct);
        if (RefusalResponse(context) is { } response)
            return response;
        var team = context.Teams.FirstOrDefault(t => t.TeamId == teamId);
        if (team is null)
            return Forbid();

        var model = new TeamSendMessageViewModel();
        PopulateModel(model, context, team);
        return View(model);
    }

    [HttpPost("{teamId:guid}/SendMessage/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid teamId, Guid id, TeamSendMessageViewModel model, CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync(ct);
        if (currentUser is null)
            return NotFound();
        var context = await teamOptions.GetContextAsync(currentUser.Id, id, ct);
        if (RefusalResponse(context) is { } response)
            return response;
        var team = context.Teams.FirstOrDefault(t => t.TeamId == teamId);
        if (team is null)
            return Forbid();

        PopulateModel(model, context, team);
        if (!ModelState.IsValid)
            return View(model);

        if (!await teamOptions.SendAsync(context, team, model.Message, model.IncludeContactInfo))
        {
            ModelState.AddModelError(string.Empty, sharedLocalizer["Common_Error"].Value);
            return View(model);
        }

        SetSuccess(string.Format(localizer["Teams_SendMessage_Success"].Value, context.Recipient!.BurnerName));
        return RedirectToProfile(context.Recipient.Id);
    }

    private IActionResult? RefusalResponse(TeamMessageContext context)
    {
        if (context.Refusal == TeamMessageRefusal.MissingUser)
            return NotFound();
        if (context.Refusal == TeamMessageRefusal.Self)
            return RedirectToProfile(context.Recipient!.Id);
        if (context.Refusal == TeamMessageRefusal.OptedOut)
        {
            SetError(localizer["Teams_SendMessage_OptedOut"].Value);
            return RedirectToProfile(context.Recipient!.Id);
        }
        return null;
    }

    private void PopulateModel(TeamSendMessageViewModel model, TeamMessageContext context, TeamMessageOption team)
    {
        model.TeamId = team.TeamId;
        model.TeamName = team.TeamName;
        model.TeamReplyToEmail = team.GoogleGroupEmail;
        model.RecipientId = context.Recipient!.Id;
        model.PageTitle = string.Format(localizer["Teams_SendMessage_FromTeam"].Value, context.Recipient.BurnerName, team.TeamName);
        model.SenderEmail = context.Sender!.Email ?? string.Empty;
    }

    private IActionResult RedirectToProfile(Guid id) =>
        RedirectToAction(ViewProfileAction, ProfileController, new { id });
}
