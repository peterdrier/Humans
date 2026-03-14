using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Humans.Infrastructure.Data;

namespace Humans.Web.Controllers;

public class UnsubscribeController : Controller
{
    private readonly HumansDbContext _db;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger<UnsubscribeController> _logger;

    public UnsubscribeController(
        HumansDbContext db,
        IDataProtectionProvider dataProtection,
        ILogger<UnsubscribeController> logger)
    {
        _db = db;
        _dataProtection = dataProtection;
        _logger = logger;
    }

    [HttpGet("/Unsubscribe/{token}")]
    public async Task<IActionResult> Index(string token)
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
        if (user == null)
            return NotFound();

        ViewData["DisplayName"] = user.DisplayName;
        return View();
    }

    [HttpPost("/Unsubscribe/{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string token)
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
        if (user == null)
            return NotFound();

        if (!user.UnsubscribedFromCampaigns)
        {
            user.UnsubscribedFromCampaigns = true;
            await _db.SaveChangesAsync();
            _logger.LogInformation("User {UserId} unsubscribed from campaign emails", userId);
        }

        return View("Done");
    }
}
