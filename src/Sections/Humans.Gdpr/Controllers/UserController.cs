using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Base;
using Humans.Base.Authorization;
using Humans.Gdpr.Models;
using Humans.Users.Contracts;

namespace Humans.Gdpr.Controllers;

/// <summary>
/// Landing pages for non-Active users — the account-status wall (Suspended/Rejected/Deleted/Merged)
/// and the pending-deletion cancel screen. Exempt from <c>MembershipRequiredFilter</c> (these
/// ARE the redirect targets), so each action self-checks the caller's state. Lives in Gdpr
/// because the deletion lifecycle (request, grace period, cancel) is a subject right;
/// the deletion work itself is Users' <see cref="IAccountDeletionService"/>.
/// </summary>
[Authorize]
[Route("User")]
internal sealed class UserController(
    IUserService userService,
    IAccountDeletionService accountDeletionService,
    IStringLocalizer<GdprResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("Status")]
    public async Task<IActionResult> Status()
    {
        var state = RoleChecks.GetUserState(User);

        // Only the excluded states see the wall; anyone else belongs in the app/onboarding.
        if (state is not (UserState.Suspended or UserState.AdminSuspended or UserState.Rejected
            or UserState.Deleted or UserState.Merged))
        {
            return RedirectToAction("Index", "Home");
        }

        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        var viewModel = new AccountStatusViewModel
        {
            State = state.Value,
            UserId = user?.Id ?? Guid.Empty,
            ContactEmail = "humans@nobodies.team",
            RejectionReason = state == UserState.Rejected ? user?.Profile?.RejectionReason : null,
        };
        return View(viewModel);
    }

    [HttpGet("Deletion")]
    public async Task<IActionResult> Deletion()
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null)
            return NotFound();

        if (!user.IsDeletionPending)
            return RedirectToAction("Index", "Home");

        var viewModel = new PendingDeletionViewModel
        {
            ScheduledFor = user.EffectiveDeletionDate?.ToDateTimeUtc(),
        };
        return View(viewModel);
    }

    [HttpPost("Deletion/Cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        var result = await accountDeletionService.CancelDeletionAsync(user.Id);
        if (result.Success)
        {
            SetSuccess(localizer["Gdpr_Deletion_Cancelled"].Value);
            return RedirectToAction("Index", "Home");
        }

        SetError(localizer["Gdpr_Deletion_NotPending"].Value);
        return RedirectToAction(nameof(Deletion));
    }
}
