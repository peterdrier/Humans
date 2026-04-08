using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Humans.Application.Extensions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
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
    private readonly HumansDbContext _dbContext;
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
        HumansDbContext dbContext,
        IClock clock,
        ILogger<GuestController> logger)
        : base(userManager)
    {
        _commPrefService = commPrefService;
        _profileService = profileService;
        _dbContext = dbContext;
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

    [HttpGet("Guest/CommunicationPreferences")]
    public async Task<IActionResult> CommunicationPreferences()
    {
        try
        {
            var user = await GetCurrentUserAsync();
            if (user is null)
                return Challenge();

            return View(await BuildCommunicationPreferencesViewModelAsync(user.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load communication preferences");
            SetError("Failed to load communication preferences.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Guest/CommunicationPreferences/Update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePreference(MessageCategory category, bool emailEnabled, bool alertEnabled)
    {
        try
        {
            var user = await GetCurrentUserAsync();
            if (user is null)
                return Unauthorized();

            if (category.IsAlwaysOn())
                return BadRequest("Cannot change always-on categories.");

            await _commPrefService.UpdatePreferenceAsync(
                user.Id, category, optedOut: !emailEnabled, inboxEnabled: alertEnabled, "Guest");

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
            var fileName = $"nobodies-data-export-{DateTime.UtcNow.ToIsoDateString()}.json";

            return File(bytes, "application/json", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export data for user {UserId}", user.Id);
            SetError("Failed to export data. Please try again.");
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
        var hasTicketOrder = await _dbContext.TicketOrders
            .AnyAsync(o => o.MatchedUserId == user.Id);

        var hasTicketAttendee = await _dbContext.TicketAttendees
            .AnyAsync(a => a.MatchedUserId == user.Id);

        if (hasTicketOrder || hasTicketAttendee)
        {
            viewModel.HasTickets = true;

            // Get ticket details for display
            var ticketOrders = await _dbContext.TicketOrders
                .Where(o => o.MatchedUserId == user.Id)
                .OrderByDescending(o => o.PurchasedAt)
                .Select(o => new GuestTicketOrderSummary
                {
                    BuyerName = o.BuyerName,
                    PurchasedAt = o.PurchasedAt.ToDateTimeUtc(),
                    AttendeeCount = o.Attendees.Count,
                    TotalAmount = o.TotalAmount,
                    Currency = o.Currency,
                })
                .ToListAsync();

            viewModel.TicketOrders = ticketOrders;
        }

        // Deletion request status
        viewModel.IsDeletionPending = user.IsDeletionPending;
        viewModel.DeletionRequestedAt = user.DeletionRequestedAt?.ToDateTimeUtc();
        viewModel.DeletionScheduledFor = user.DeletionScheduledFor?.ToDateTimeUtc();

        return viewModel;
    }

    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId)
    {
        var prefs = await _commPrefService.GetPreferencesAsync(userId);
        var prefsByCategory = prefs.ToDictionary(p => p.Category);

        var hasTicketOrder = await _dbContext.TicketOrders
            .AnyAsync(o => o.MatchedUserId == userId);

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
                    ? $"Ticketing — {DateTime.UtcNow.Year}"
                    : category.ToDisplayName(),
                Description = category.ToDescription(),
                EmailEnabled = pref is null || !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = !isAlwaysOn && !isTicketingLocked,
                AlertEditable = !isAlwaysOn && !isTicketingLocked,
                Note = isTicketingLocked ? "Locked — you have a ticket order for this year" : null,
            });
        }

        return new CommunicationPreferencesViewModel { Categories = categories };
    }
}
