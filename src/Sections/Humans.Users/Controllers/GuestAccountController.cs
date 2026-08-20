using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Base.Extensions;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Models;
using NodaTime;

namespace Humans.Users.Controllers;

/// <summary>
/// Self-service comms preferences and GDPR-erasure actions for profileless accounts
/// (authenticated users without a Profile). Moved from Shell's GuestController
/// (nobodies-collective/Humans#1091) — the dashboard frame itself lives in
/// Humans.Onboarding, the data export lives in Humans.Gdpr; these actions stayed
/// here because both call this section's own services (<see cref="ICommunicationPreferenceService"/>,
/// <see cref="IAccountDeletionService"/>) directly.
/// </summary>
[Authorize]
internal sealed class GuestAccountController(
    IUserServiceRead userService,
    ICommunicationPreferenceService commPrefService,
    ITicketServiceRead ticketQueryService,
    IAccountDeletionService accountDeletionService,
    IClock clock,
    ILogger<GuestAccountController> logger) : HumansControllerBase(userService)
{
    // WARNING: [AllowAnonymous] — accepts unauthenticated requests with a valid unsubscribe
    // token (utoken). The token scopes access to THIS page only. Do not add links to other
    // authenticated pages from the token-mode view. See EndpointAuthorizationTests allowlist.
    [HttpGet("Guest/CommunicationPreferences")]
    [AllowAnonymous]
    public async Task<IActionResult> CommunicationPreferences(string? utoken)
    {
        try
        {
            var (userId, tokenCategory, _) = await ResolveUserIdOrTokenAsync(utoken);
            if (userId is null)
                return Challenge();

            var model = await BuildCommunicationPreferencesViewModelAsync(userId.Value);
            model.UnsubscribeToken = utoken;
            model.HighlightCategory = tokenCategory;
            return View(model);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load communication preferences");
            SetError("Failed to load communication preferences.");
            return RedirectToAction("Index", "Guest");
        }
    }

    // WARNING: [AllowAnonymous] — paired with CommunicationPreferences GET above.
    // See EndpointAuthorizationTests allowlist.
    [HttpPost("Guest/CommunicationPreferences/Update")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePreference(MessageCategory category, bool emailEnabled, bool alertEnabled, string? utoken)
    {
        try
        {
            var (userId, _, fromToken) = await ResolveUserIdOrTokenAsync(utoken);
            if (userId is null)
                return Unauthorized();

            if (!CanUpdatePreference(category))
                return BadRequest("Cannot change always-on categories.");

            await commPrefService.UpdatePreferenceAsync(
                userId.Value, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, GetPreferenceUpdateSource(fromToken));

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

    [HttpPost("Guest/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        try
        {
            // Single orchestrator for profile + profileless deletion (see #685).
            var result = await accountDeletionService.RequestDeletionAsync(user.Id);
            var flash = GuestDeletionRequestFlash.From(result);
            if (!flash.Success)
            {
                SetError(flash.Message);
                return RedirectToAction("Index", "Guest");
            }

            SetSuccess(flash.Message);

            return RedirectToAction("Index", "Guest");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process deletion request for user {UserId}", user.Id);
            SetError("Failed to process deletion request. Please try again.");
            return RedirectToAction("Index", "Guest");
        }
    }

    [HttpPost("Guest/CancelDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        var result = await accountDeletionService.CancelDeletionAsync(user.Id);
        if (!result.Success)
        {
            SetError(string.Equals(result.ErrorKey, "NoDeletionPending", StringComparison.Ordinal)
                ? "No deletion request is pending."
                : "Failed to cancel deletion request. Please try again.");
            return RedirectToAction("Index", "Guest");
        }

        SetSuccess("Deletion request cancelled.");
        return RedirectToAction("Index", "Guest");
    }

    /// <summary>Resolves user from session, else unsubscribe token. FromToken=true → MagicLink source.</summary>
    private async Task<(Guid? UserId, MessageCategory? TokenCategory, bool FromToken)> ResolveUserIdOrTokenAsync(string? utoken)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is not null)
            return (user.Id, null, false);

        if (string.IsNullOrEmpty(utoken))
            return (null, null, false);

        var result = commPrefService.ValidateUnsubscribeToken(utoken);
        if (result.Status != TokenValidationStatus.Valid)
            return (null, null, false);

        var exists = await FindUserInfoByIdAsync(result.UserId);
        return exists is not null
            ? (result.UserId, result.Category, true)
            : (null, null, false);
    }

    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId)
    {
        var prefs = await commPrefService.GetPreferencesReadOnlyAsync(userId);
        var prefsByCategory = prefs.ToDictionary(p => p.Category);

        var hasTicketOrder = (await ticketQueryService.GetUserTicketHoldingsAsync(userId))
            .HasTicketAttendeeMatch;

        var categories = new List<CategoryPreferenceItem>();

        foreach (var category in MessageCategoryExtensions.ActiveCategories)
        {
            var pref = prefsByCategory.GetValueOrDefault(category);
            var isAlwaysOn = category.IsAlwaysOn();
            var isTicketingLocked = category == MessageCategory.Ticketing && hasTicketOrder;

            categories.Add(new CategoryPreferenceItem
            {
                Category = category,
                DisplayName = category == MessageCategory.Ticketing
                    ? $"Ticketing — {clock.GetCurrentInstant().InUtc().Year}"
                    : category.ToDisplayName(),
                Description = category.ToDescription(),
                // No row → category's domain default (Marketing is opt-out-by-default,
                // so a missing row renders unchecked). Matches the panel view component.
                EmailEnabled = pref is null ? !category.DefaultOptedOut() : !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = !isAlwaysOn && !isTicketingLocked,
                AlertEditable = !isAlwaysOn && !isTicketingLocked,
                Note = isTicketingLocked ? "Locked — you have a ticket for this year" : null,
            });
        }

        return new CommunicationPreferencesViewModel { Categories = categories };
    }
}
