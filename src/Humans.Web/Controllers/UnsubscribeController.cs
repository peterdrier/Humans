using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Humans.Infrastructure.Data;

namespace Humans.Web.Controllers;

public class UnsubscribeController : Controller
{
    private readonly HumansDbContext _db;
    private readonly ICommunicationPreferenceService _preferenceService;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<UnsubscribeController> _logger;

    public UnsubscribeController(
        HumansDbContext db,
        ICommunicationPreferenceService preferenceService,
        IDataProtectionProvider dataProtection,
        ILogger<UnsubscribeController> logger)
    {
        _db = db;
        _preferenceService = preferenceService;
        _dataProtection = dataProtection;
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

            ViewData["DisplayName"] = user.DisplayName;
            ViewData["CategoryName"] = GetCategoryDisplayName(category);
            ViewData["Token"] = token;
            return View();
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

            ViewData["CategoryName"] = GetCategoryDisplayName(category);
            return View("Done");
        }

        // Fall back to legacy campaign-only token
        return await TryLegacyConfirm(token);
    }

    /// <summary>
    /// RFC 8058 one-click unsubscribe endpoint.
    /// Email clients POST List-Unsubscribe=One-Click — no anti-forgery token.
    /// </summary>
    [HttpPost("/Unsubscribe/OneClick")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> OneClick([FromForm(Name = "token")] string token)
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

        ViewData["DisplayName"] = user.DisplayName;
        ViewData["CategoryName"] = GetCategoryDisplayName(MessageCategory.Marketing);
        ViewData["Token"] = token;
        return View();
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

        ViewData["CategoryName"] = GetCategoryDisplayName(MessageCategory.Marketing);
        return View("Done");
    }

    private static string GetCategoryDisplayName(MessageCategory category) => category switch
    {
        MessageCategory.System => "System",
        MessageCategory.EventOperations => "Event Operations",
        MessageCategory.CommunityUpdates => "Community Updates",
        MessageCategory.Marketing => "Marketing",
        _ => category.ToString(),
    };
}
