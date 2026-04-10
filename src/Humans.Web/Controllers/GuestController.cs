using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>
/// Dashboard for profileless accounts (authenticated users without a Profile).
/// Provides comms preferences, GDPR tools, ticket status, and create-profile CTA.
/// </summary>
[Authorize]
public class GuestController : HumansControllerBase
{
    private readonly ICommunicationPreferenceService _commPrefService;
    private readonly IProfileService _profileService;
    private readonly ITicketQueryService _ticketQueryService;
    private readonly IClock _clock;
    private readonly ILogger<GuestController> _logger;

    private static readonly System.Text.Json.JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public GuestController(
        UserManager<User> userManager,
        ICommunicationPreferenceService commPrefService,
        IProfileService profileService,
        ITicketQueryService ticketQueryService,
        IClock clock,
        ILogger<GuestController> logger)
        : base(userManager)
    {
        _commPrefService = commPrefService;
        _profileService = profileService;
        _ticketQueryService = ticketQueryService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        try
        {
            var viewModel = await BuildDashboardViewModelAsync(user);
            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Guest dashboard for user {UserId}", user.Id);
            return View(new GuestDashboardViewModel { DisplayName = user.DisplayName });
        }
    }

    // ─── Communication Preferences ───────────────────────────────────

    // WARNING: [AllowAnonymous] — accepts unauthenticated requests with a valid unsubscribe
    // token (utoken). The token scopes access to THIS page only. Do not add links to other
    // authenticated pages from the token-mode view. See EndpointAuthorizationTests allowlist.
    [HttpGet("Guest/CommunicationPreferences")]
    [AllowAnonymous]
    public async Task<IActionResult> CommunicationPreferences(string? utoken)
    {
        try
        {
            var userId = await ResolveUserIdOrTokenAsync(utoken);
            if (userId is null)
                return Challenge();

            var model = await BuildCommunicationPreferencesViewModelAsync(userId.Value);
            model.UnsubscribeToken = utoken;
            return View(model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load communication preferences");
            SetError("Failed to load communication preferences.");
            return RedirectToAction(nameof(Index));
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
            var userId = await ResolveUserIdOrTokenAsync(utoken);
            if (userId is null)
                return Unauthorized();

            if (category.IsAlwaysOn())
                return BadRequest("Cannot change always-on categories.");

            await _commPrefService.UpdatePreferenceAsync(
                userId.Value, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, "Guest");

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save communication preference for {Category}", category);
            return StatusCode(500);
        }
    }

    // ─── GDPR Data Export ────────────────────────────────────────────

    [HttpGet("Guest/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return Challenge();

        try
        {
            var exportData = await _profileService.ExportDataAsync(user.Id);

            var json = System.Text.Json.JsonSerializer.Serialize(exportData, ExportJsonOptions);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var fileName = $"nobodies-data-export-{_clock.GetCurrentInstant().ToDateTimeUtc().ToIsoDateString()}.json";

            return File(bytes, "application/json", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export data for user {UserId}", user.Id);
            SetError("Failed to export data. Please try again.");
            return RedirectToAction(nameof(Index));
        }
    }

    // ─── GDPR Deletion Request ─────────────────────────────────────

    [HttpPost("Guest/RequestDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestDeletion()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return Challenge();

        try
        {
            if (user.IsDeletionPending)
            {
                SetError("A deletion request is already pending.");
                return RedirectToAction(nameof(Index));
            }

            var now = _clock.GetCurrentInstant();
            var gracePeriod = Duration.FromDays(30);

            // Check for upcoming event tickets — if ticket holder, set EligibleAfter to post-event
            var eventHoldDate = await _profileService.GetEventHoldDateAsync(user.Id);

            user.DeletionRequestedAt = now;
            user.DeletionScheduledFor = now.Plus(gracePeriod);
            user.DeletionEligibleAfter = eventHoldDate;

            await UpdateCurrentUserAsync(user);

            if (eventHoldDate.HasValue)
            {
                SetSuccess(
                    $"Deletion request recorded. Because you have tickets for an upcoming event, " +
                    $"your account will be deleted after {eventHoldDate.Value.ToDateTimeUtc():d MMM yyyy}.");
            }
            else
            {
                SetSuccess(
                    $"Deletion request recorded. Your account will be permanently deleted on " +
                    $"{user.DeletionScheduledFor.Value.ToDateTimeUtc():d MMM yyyy}.");
            }

            _logger.LogWarning(
                "Profileless user {UserId} requested account deletion. Scheduled: {ScheduledFor}, EligibleAfter: {EligibleAfter}",
                user.Id, user.DeletionScheduledFor, eventHoldDate);

            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process deletion request for user {UserId}", user.Id);
            SetError("Failed to process deletion request. Please try again.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Guest/CancelDeletion")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelDeletion()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return Challenge();

        try
        {
            if (!user.IsDeletionPending)
            {
                SetError("No deletion request is pending.");
                return RedirectToAction(nameof(Index));
            }

            user.DeletionRequestedAt = null;
            user.DeletionScheduledFor = null;
            user.DeletionEligibleAfter = null;

            await UpdateCurrentUserAsync(user);

            SetSuccess("Deletion request cancelled.");
            _logger.LogInformation("Profileless user {UserId} cancelled deletion request", user.Id);

            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel deletion request for user {UserId}", user.Id);
            SetError("Failed to cancel deletion request. Please try again.");
            return RedirectToAction(nameof(Index));
        }
    }

    // ─── Private Helpers ─────────────────────────────────────────────

    private async Task<GuestDashboardViewModel> BuildDashboardViewModelAsync(User user)
    {
        var viewModel = new GuestDashboardViewModel
        {
            DisplayName = user.DisplayName,
        };

        // Ticket status: check for matched ticket orders and attendees
        var hasTickets = await _ticketQueryService.HasTicketAttendeeMatchAsync(user.Id);

        if (hasTickets)
        {
            viewModel.HasTickets = true;

            // Get ticket details for display
            var orderSummaries = await _ticketQueryService.GetUserTicketOrderSummariesAsync(user.Id);
            viewModel.TicketOrders = orderSummaries
                .Select(s => new GuestTicketOrderSummary
                {
                    BuyerName = s.BuyerName,
                    PurchasedAt = s.PurchasedAt.ToDateTimeUtc(),
                    AttendeeCount = s.AttendeeCount,
                    TotalAmount = s.TotalAmount,
                    Currency = s.Currency,
                })
                .ToList();
        }

        // Deletion request status
        viewModel.IsDeletionPending = user.IsDeletionPending;
        viewModel.DeletionRequestedAt = user.DeletionRequestedAt?.ToDateTimeUtc();
        viewModel.DeletionScheduledFor = user.DeletionScheduledFor?.ToDateTimeUtc();
        viewModel.DeletionEligibleAfter = user.DeletionEligibleAfter?.ToDateTimeUtc();

        return viewModel;
    }

    /// <summary>
    /// Resolves the user ID from the current session, or falls back to an unsubscribe token.
    /// Returns null if neither is available.
    /// </summary>
    private async Task<Guid?> ResolveUserIdOrTokenAsync(string? utoken)
    {
        // Prefer authenticated session
        var user = await GetCurrentUserAsync();
        if (user is not null)
            return user.Id;

        // Fall back to unsubscribe token
        if (string.IsNullOrEmpty(utoken))
            return null;

        var result = _commPrefService.ValidateUnsubscribeToken(utoken);
        if (result is null)
            return null;

        var (userId, _) = result.Value;

        // Verify user still exists
        var exists = await FindUserByIdAsync(userId);
        return exists is not null ? userId : null;
    }

    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId)
    {
        var prefs = await _commPrefService.GetPreferencesAsync(userId);
        var prefsByCategory = prefs.ToDictionary(p => p.Category);

        var hasTicketOrder = await _ticketQueryService.HasTicketAttendeeMatchAsync(userId);

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
                    ? $"Ticketing — {_clock.GetCurrentInstant().InUtc().Year}"
                    : category.ToDisplayName(),
                Description = category.ToDescription(),
                EmailEnabled = pref is null || !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = !isAlwaysOn && !isTicketingLocked,
                AlertEditable = !isAlwaysOn && !isTicketingLocked,
                Note = isTicketingLocked ? "Locked — you have a ticket for this year" : null,
            });
        }

        return new CommunicationPreferencesViewModel { Categories = categories };
    }
}
