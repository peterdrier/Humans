using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Web.Models;
using NodaTime;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Profiles;

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
    private readonly IGdprExportService _gdprExportService;
    private readonly IOnboardingWidgetState _widgetState;
    private readonly IAccountDeletionService _accountDeletionService;
    private readonly IClock _clock;
    private readonly ILogger<GuestController> _logger;

    private static readonly System.Text.Json.JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public GuestController(
        IUserService userService,
        ICommunicationPreferenceService commPrefService,
        IProfileService profileService,
        ITicketQueryService ticketQueryService,
        IGdprExportService gdprExportService,
        IOnboardingWidgetState widgetState,
        IAccountDeletionService accountDeletionService,
        IClock clock,
        ILogger<GuestController> logger)
        : base(userService)
    {
        _commPrefService = commPrefService;
        _profileService = profileService;
        _ticketQueryService = ticketQueryService;
        _gdprExportService = gdprExportService;
        _widgetState = widgetState;
        _accountDeletionService = accountDeletionService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
        {
            return Challenge();
        }

        var step = await _widgetState.GetCurrentStepAsync(user.Id, cancellationToken);
        if (step != OnboardingWidgetStep.Complete)
        {
            return RedirectToAction("Index", "OnboardingWidget");
        }

        try
        {
            var viewModel = await BuildDashboardViewModelAsync(user);
            return View(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Guest dashboard for user {UserId}", user.Id);
            return View(new GuestDashboardViewModel { DisplayName = user.BurnerName });
        }
    }

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
            var (userId, _, fromToken) = await ResolveUserIdOrTokenAsync(utoken);
            if (userId is null)
                return Unauthorized();

            if (!CanUpdatePreference(category))
                return BadRequest("Cannot change always-on categories.");

            await _commPrefService.UpdatePreferenceAsync(
                userId.Value, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, GetPreferenceUpdateSource(fromToken));

            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save communication preference for {Category}", category);
            return StatusCode(500);
        }
    }

    private static bool CanUpdatePreference(MessageCategory category) => !category.IsAlwaysOn();

    private static string GetPreferenceUpdateSource(bool fromToken) => fromToken ? "MagicLink" : "Guest";

    [HttpGet("Guest/DownloadData")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> DownloadData(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        try
        {
            var export = await _gdprExportService.ExportForUserAsync(user.Id, ct);

            var payload = BuildExportPayload(export);
            var json = System.Text.Json.JsonSerializer.Serialize(payload, ExportJsonOptions);
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

    private static Dictionary<string, object?> BuildExportPayload(GdprExport export)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ExportedAt"] = export.ExportedAt
        };
        foreach (var (section, data) in export.Sections)
        {
            payload[section] = data;
        }
        return payload;
    }

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
            var result = await _accountDeletionService.RequestDeletionAsync(user.Id);
            var flash = GuestDeletionRequestFlash.From(result);
            if (!flash.Success)
            {
                SetError(flash.Message);
                return RedirectToAction(nameof(Index));
            }

            SetSuccess(flash.Message);

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
        var user = await GetCurrentUserInfoAsync();
        if (user is null)
            return Challenge();

        var result = await _accountDeletionService.CancelDeletionAsync(user.Id);
        if (!result.Success)
        {
            SetError(string.Equals(result.ErrorKey, "NoDeletionPending", StringComparison.Ordinal)
                ? "No deletion request is pending."
                : "Failed to cancel deletion request. Please try again.");
            return RedirectToAction(nameof(Index));
        }

        SetSuccess("Deletion request cancelled.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<GuestDashboardViewModel> BuildDashboardViewModelAsync(UserInfo user)
    {
        var viewModel = new GuestDashboardViewModel
        {
            DisplayName = user.BurnerName,
        };

        var hasTickets = await _ticketQueryService.HasTicketAttendeeMatchAsync(user.Id);

        if (hasTickets)
        {
            viewModel.HasTickets = true;

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

        viewModel.IsDeletionPending = user.IsDeletionPending;
        viewModel.DeletionRequestedAt = user.DeletionRequestedAt?.ToDateTimeUtc();
        viewModel.DeletionScheduledFor = user.DeletionScheduledFor?.ToDateTimeUtc();
        viewModel.DeletionEligibleAfter = user.DeletionEligibleAfter?.ToDateTimeUtc();

        return viewModel;
    }

    /// <summary>Resolves user from session, else unsubscribe token. FromToken=true → MagicLink source.</summary>
    private async Task<(Guid? UserId, MessageCategory? TokenCategory, bool FromToken)> ResolveUserIdOrTokenAsync(string? utoken)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is not null)
            return (user.Id, null, false);

        if (string.IsNullOrEmpty(utoken))
            return (null, null, false);

        var result = _commPrefService.ValidateUnsubscribeToken(utoken);
        if (result.Status != TokenValidationStatus.Valid)
            return (null, null, false);

        var exists = await FindUserInfoByIdAsync(result.UserId);
        return exists is not null
            ? (result.UserId, result.Category, true)
            : (null, null, false);
    }

    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId)
    {
        var prefs = await _commPrefService.GetPreferencesReadOnlyAsync(userId);
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
