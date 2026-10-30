using Humans.Base.Attributes;
using Humans.Base.Controllers;
using Humans.Email.Contracts;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;

namespace Humans.Email.Controllers;

/// <summary>The signed-in member's own preferences only; never takes a user id.</summary>
[Authorize]
[Route("Email")]
internal sealed class CommunicationPreferencesController(
    IUserService userService,
    ICommunicationPreferenceManagement commPrefService,
    ILogger<CommunicationPreferencesController> logger,
    IStringLocalizer<EmailResource> localizer) : HumansControllerBase(userService)
{
    [HttpGet("CommunicationPreferences")]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> Index()
    {
        try
        {
            var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
            if (user is null)
                return NotFound();

            return View(model: user.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to load communication preferences");
            SetError(localizer["Email_CommPrefs_LoadFailed"].Value);
            return RedirectToAction("Me", "Profile");
        }
    }

    [HttpPost("CommunicationPreferences/Update")]
    [ValidateAntiForgeryToken]
    [AllowBeforeActiveMembership]
    public async Task<IActionResult> UpdatePreference(MessageCategory category, [BindRequired] bool emailEnabled, [BindRequired] bool alertEnabled)
    {
        try
        {
            var user = await GetCurrentUserInfoAsync();
            if (user is null)
                return Unauthorized();
            if (!ModelState.IsValid)
            {
                logger.LogWarning("Rejected malformed communication preference update for {UserId}", user.Id);
                return BadRequest(ModelState);
            }

            if (category.IsAlwaysOn())
                return BadRequest(localizer["Email_CommPrefs_AlwaysOnCategoryCannotChange"].Value);

            if (!await commPrefService.UpdatePreferenceAsync(
                    user.Id, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, "Profile", actorUserId: user.Id))
                return BadRequest(localizer["Email_CommPrefs_TicketingLockedCannotChange"].Value);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save communication preference for {Category}", category);
            return StatusCode(500);
        }
    }
}
