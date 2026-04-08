using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Humans.Infrastructure.Data;

namespace Humans.Web.Controllers;

public class UnsubscribeController : Controller
{
    private readonly HumansDbContext _db;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly SignInManager<User> _signInManager;
    private readonly ILogger<UnsubscribeController> _logger;

    public UnsubscribeController(
        HumansDbContext db,
        ICommunicationPreferenceService preferenceService,
        IDataProtectionProvider dataProtection,
        SignInManager<User> signInManager,
        ILogger<UnsubscribeController> logger)
    {
        _db = db;
        _preferenceService = preferenceService;
        _dataProtection = dataProtection;
        _signInManager = signInManager;
        _logger = logger;
    }

    [HttpGet("/Unsubscribe/{token}")]
    public async Task<IActionResult> Index(string token)
    {
        // Try new category-aware token first
        var result = _preferenceService.ValidateUnsubscribeToken(token);
        if (result is not null)
        {
            var (userId, category) = result.Value;
            var user = await _db.Users.FindAsync(userId);
            if (user is null)
                return NotFound();

            // Sign in the user and redirect to communication preferences
            await _signInManager.SignInAsync(user, isPersistent: false);
            _logger.LogInformation(
                "User {UserId} authenticated via unsubscribe link for {Category}",
                userId, category);

            return RedirectToCommunicationPreferences(user);
        }

        // Fall back to legacy campaign-only token
        return await TryLegacyToken(token);
    }

    [HttpPost("/Unsubscribe/{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string token)
    {
        // Try new category-aware token first
        var result = _preferenceService.ValidateUnsubscribeToken(token);
        if (result is not null)
        {
            var (userId, category) = result.Value;
            var user = await _db.Users.FindAsync(userId);
            if (user is null)
                return NotFound();

            await _preferenceService.UpdatePreferenceAsync(userId, category, optedOut: true, source: "MagicLink");

            // Sign in and redirect to communication preferences
            await _signInManager.SignInAsync(user, isPersistent: false);
            _logger.LogInformation(
                "User {UserId} unsubscribed from {Category} and authenticated via unsubscribe link",
                userId, category);

            return RedirectToCommunicationPreferences(user);
        }

        // Fall back to legacy campaign-only token
        return await TryLegacyConfirm(token);
    }

    /// <summary>
    /// RFC 8058 one-click unsubscribe endpoint.
    /// Email clients POST List-Unsubscribe=One-Click to the URL in the List-Unsubscribe header,
    /// which includes the token as a query parameter. No anti-forgery token required.
    /// </summary>
    [HttpPost("/Unsubscribe/OneClick")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OneClick([FromQuery] string token)
    {
        try
        {
            var result = _preferenceService.ValidateUnsubscribeToken(token);
            if (result is null)
                return BadRequest();

            var (userId, category) = result.Value;
            var user = await _db.Users.FindAsync(userId);
            if (user is null)
                return NotFound();

            await _preferenceService.UpdatePreferenceAsync(userId, category, optedOut: true, source: "OneClick");

            _logger.LogInformation("RFC 8058 one-click unsubscribe: user {UserId} from {Category}", userId, category);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process RFC 8058 one-click unsubscribe");
            return StatusCode(500);
        }
    }

    /// <summary>
    /// Redirects to the appropriate communication preferences page based on whether the user has a profile.
    /// Profileless users go to Guest/CommunicationPreferences; users with profiles go to Profile/CommunicationPreferences.
    /// </summary>
    private IActionResult RedirectToCommunicationPreferences(User user)
    {
        var hasProfile = _db.Profiles.Any(p => p.UserId == user.Id);
        return hasProfile
            ? RedirectToAction(nameof(ProfileController.CommunicationPreferences), "Profile")
            : RedirectToAction(nameof(GuestController.CommunicationPreferences), "Guest");
    }

    private async Task<IActionResult> TryLegacyToken(string token)
    {
        var protector = _dataProtection
            .CreateProtector("CampaignUnsubscribe")
            .ToTimeLimitedDataProtector();

        Guid userId;
        try
        {
            var userIdString = protector.Unprotect(token);
            userId = Guid.Parse(userIdString);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Unsubscribe token was expired or invalid");
            if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                return View("Expired");
            return NotFound();
        }

        var user = await _db.Users.FindAsync(userId);
        if (user is null)
            return NotFound();

        // Sign in the user and redirect to communication preferences
        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation(
            "User {UserId} authenticated via legacy unsubscribe link",
            userId);

        return RedirectToCommunicationPreferences(user);
    }

    private async Task<IActionResult> TryLegacyConfirm(string token)
    {
        var protector = _dataProtection
            .CreateProtector("CampaignUnsubscribe")
            .ToTimeLimitedDataProtector();

        Guid userId;
        try
        {
            var userIdString = protector.Unprotect(token);
            userId = Guid.Parse(userIdString);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Unsubscribe token was expired or invalid on POST");
            if (ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                return View("Expired");
            return NotFound();
        }

        var user = await _db.Users.FindAsync(userId);
        if (user is null)
            return NotFound();

        // Use the new preference service for legacy tokens too
        await _preferenceService.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true, source: "MagicLink");

        // Sign in and redirect to communication preferences
        await _signInManager.SignInAsync(user, isPersistent: false);
        _logger.LogInformation(
            "User {UserId} unsubscribed from Marketing and authenticated via legacy unsubscribe link",
            userId);

        return RedirectToCommunicationPreferences(user);
    }
}
