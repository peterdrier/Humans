using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

/// <summary>
/// Landing pages for non-Active users — the account-status wall (Suspended/Rejected/Deleted/Merged)
/// and the pending-deletion cancel screen. Exempt from <see cref="MembershipRequiredFilter"/> (these
/// ARE the redirect targets), so each action self-checks the caller's state.
/// </summary>
[Authorize]
[Route("User")]
public class UserController(
    IUserServiceRead userService,
    IAccountDeletionService accountDeletionService,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("Status")]
    public async Task<IActionResult> Status()
    {
        var state = RoleAssignmentClaimsTransformation.GetUserState(User);

        // Only the excluded states see the wall; anyone else belongs in the app/onboarding.
        if (state is not (UserState.Suspended or UserState.AdminSuspended or UserState.Rejected
            or UserState.Deleted or UserState.Merged))
        {
            return RedirectToAction("Index", "Home");
        }

        var user = await GetCurrentUserInfoAsync();
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
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return NotFound();

        if (!user.IsDeletionPending)
            return RedirectToAction("Index", "Home");

        var viewModel = new PendingDeletionViewModel
        {
            ScheduledFor = user.DeletionScheduledFor?.ToDateTimeUtc(),
        };
        return View(viewModel);
    }

    // Moved from ProfileController per the Profile* retirement — the cancel-deletion lever lives
    // with the User section now.
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
            SetSuccess(localizer["Profile_DeletionCancelled"].Value);
            return RedirectToAction("Index", "Home");
        }

        SetError(localizer["Profile_NoDeletionPending"].Value);
        return RedirectToAction(nameof(Deletion));
    }
}
