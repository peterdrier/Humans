using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.GoogleIntegration.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Humans.GoogleIntegration.Controllers;

/// <summary>
/// Designates which verified address is a member's Google Workspace identity, from the Emails grid
/// (the member's own <c>/Profile/Me/Emails</c> and the admin grid <c>/Profile/{id}/Admin/Emails</c>).
/// Same self-or-HumanAdmin/Board/Admin gate as the grid. Moved out of Users'
/// <c>ProfileEmailsController</c> in Users decomposition commit 15.
/// </summary>
[Authorize]
[Route("Profile")]
internal sealed class WorkspaceIdentityController(
    IUserService userService,
    IWorkspaceIdentityManagement identities,
    IStringLocalizer<GoogleIntegrationResource> localizer,
    ILogger<WorkspaceIdentityController> logger) : HumansControllerBase(userService)
{
    [HttpPost("Me/Emails/Google")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Set(Guid emailId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null)
            return NotFound();

        await SetAsync(user, emailId, user.Id, ct);
        return RedirectToAction("Emails", "ProfileEmails");
    }

    [HttpPost("{id:guid}/Admin/Emails/Google")]
    [Authorize(Policy = PolicyNames.HumanAdminBoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AdminSet(Guid id, Guid emailId, CancellationToken ct)
    {
        var actor = await GetCurrentUserInfoAsync(ct);
        if (actor is null)
            return Forbid();

        var target = await FindUserInfoByIdAsync(id, ct);
        if (target is null || target.Id != id)
            return NotFound();

        await SetAsync(target, emailId, actor.Id, ct);
        return RedirectToAction("AdminEmails", "ProfileEmails", new { id });
    }

    private async Task SetAsync(UserInfo target, Guid emailId, Guid actorId, CancellationToken ct)
    {
        var address = target.UserEmails.FirstOrDefault(e => e.Id == emailId)?.Email;
        var ok = address is not null && await identities.SetAsync(target.Id, address, actorId, ct);
        if (ok)
        {
            SetSuccess(localizer["GoogleIntegration_Identity_Updated"].Value);
            return;
        }

        logger.LogWarning("Google identity not set to email {EmailId} for user {UserId}", emailId, target.Id);
        SetError(localizer["GoogleIntegration_Identity_SetRejected"].Value);
    }
}
