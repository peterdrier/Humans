using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Email.Controllers;

/// <summary>For profileless accounts and for anyone arriving from an unsubscribe link.</summary>
[Authorize]
internal sealed class GuestAccountController(
    IUserService userService,
    ICommunicationPreferenceManagement commPrefService,
    IEnumerable<ICommunicationPreferenceLock> locks,
    IUnsubscribeService unsubscribeService,
    IClock clock,
    ILogger<GuestAccountController> logger,
    IStringLocalizer<EmailResource> localizer) : HumansControllerBase(userService)
{
    // WARNING: [AllowAnonymous] — accepts unauthenticated requests with a valid unsubscribe
    // token (utoken). The token scopes access to THIS page only. Do not add links to other
    // authenticated pages from the token-mode view. See EndpointAuthorizationTests allowlist.
    [HttpGet("Email/Guest/CommunicationPreferences")]
    [AllowAnonymous]
    public async Task<IActionResult> CommunicationPreferences(string? utoken)
    {
        try
        {
            var (userId, tokenCategory, _) = await ResolveUserIdOrTokenAsync(utoken, HttpContext.RequestAborted);
            if (userId is null)
                return Challenge();

            var model = await BuildCommunicationPreferencesViewModelAsync(userId.Value, HttpContext.RequestAborted);
            model.UnsubscribeToken = utoken;
            model.HighlightCategory = tokenCategory;
            return View(model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to load communication preferences");
            SetError(localizer["Email_CommPrefs_LoadFailed"].Value);
            return RedirectToAction("Index", "Guest");
        }
    }

    // WARNING: [AllowAnonymous] — paired with CommunicationPreferences GET above.
    // See EndpointAuthorizationTests allowlist.
    [HttpPost("Email/Guest/CommunicationPreferences/Update")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePreference(MessageCategory category, [BindRequired] bool emailEnabled, [BindRequired] bool alertEnabled, string? utoken)
    {
        try
        {
            var (userId, _, fromToken) = await ResolveUserIdOrTokenAsync(utoken);
            if (userId is null)
                return Unauthorized();
            if (!ModelState.IsValid)
            {
                logger.LogWarning("Rejected malformed communication preference update for {UserId}", userId.Value);
                return BadRequest(ModelState);
            }

            if (!CanUpdatePreference(category))
                return BadRequest(localizer["Email_CommPrefs_AlwaysOnCategoryCannotChange"].Value);

            if (!await commPrefService.UpdatePreferenceAsync(
                    userId.Value, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, GetPreferenceUpdateSource(fromToken),
                    actorUserId: fromToken ? null : userId))
                return BadRequest(localizer["Email_CommPrefs_TicketingLockedCannotChange"].Value);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save communication preference for {Category}", category);
            return StatusCode(500);
        }
    }

    private static bool CanUpdatePreference(MessageCategory category) => !category.IsAlwaysOn();

    private static string GetPreferenceUpdateSource(bool fromToken) => fromToken ? "MagicLink" : "Guest";

    /// <summary>Resolves user from session, else unsubscribe token. FromToken=true → MagicLink source.</summary>
    private async Task<(Guid? UserId, MessageCategory? TokenCategory, bool FromToken)> ResolveUserIdOrTokenAsync(string? utoken, CancellationToken ct = default)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is not null)
            return user.IsGdprAnonymized ? (null, null, false) : (user.Id, null, false);

        if (string.IsNullOrEmpty(utoken))
            return (null, null, false);

        var result = await unsubscribeService.ValidateTokenAsync(utoken, ct);
        return result.IsValid && !result.IsLegacy
            ? (result.UserId, result.Category, true)
            : (null, null, false);
    }

    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId, CancellationToken ct)
    {
        var prefs = await commPrefService.GetPreferencesReadOnlyAsync(userId, ct);
        var locked = await CommunicationPreferenceService.GetLockedCategoriesAsync(locks, userId, ct);

        return new CommunicationPreferencesViewModel
        {
            Categories = CategoryPreferenceItem.ForActiveCategories(prefs, locked),
            TicketingYear = clock.GetCurrentInstant().InUtc().Year,
        };
    }
}
