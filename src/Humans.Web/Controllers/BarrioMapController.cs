using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("BarrioMap")]
public class BarrioMapController : Controller
{
    private readonly ICampMapService _campMapService;
    private readonly UserManager<User> _userManager;

    public BarrioMapController(ICampMapService campMapService, UserManager<User> userManager)
    {
        _campMapService = campMapService;
        _userManager = userManager;
    }

    private Guid CurrentUserId() => Guid.Parse(_userManager.GetUserId(User)!);

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var settings = await _campMapService.GetSettingsAsync(cancellationToken);
        var isMapAdmin = await _campMapService.IsUserMapAdminAsync(userId, cancellationToken);
        var userSeasonId = await _campMapService.GetUserCampSeasonIdForYearAsync(userId, settings.Year, cancellationToken);
        var seasonsWithout = await _campMapService.GetCampSeasonsWithoutCampPolygonAsync(settings.Year, cancellationToken);

        ViewBag.IsPlacementOpen = settings.IsPlacementOpen;
        ViewBag.IsMapAdmin = isMapAdmin;
        ViewBag.UserCampSeasonId = userSeasonId?.ToString() ?? string.Empty;
        ViewBag.CurrentUserId = userId.ToString();
        ViewBag.SeasonsWithoutCampPolygon = seasonsWithout;
        ViewBag.Year = settings.Year;
        ViewBag.PlacementOpensAt = settings.PlacementOpensAt;
        ViewBag.PlacementClosesAt = settings.PlacementClosesAt;

        return View();
    }

    [HttpGet("Admin")]
    public async Task<IActionResult> Admin(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        ViewBag.Settings = await _campMapService.GetSettingsAsync(cancellationToken);
        return View();
    }

    [HttpPost("Admin/OpenPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenPlacement(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        await _campMapService.OpenPlacementAsync(userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/ClosePlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePlacement(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        await _campMapService.ClosePlacementAsync(userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/UploadLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLimitZone(IFormFile file, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        using var reader = new StreamReader(file.OpenReadStream());
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        await _campMapService.UpdateLimitZoneAsync(geoJson, userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/UpdatePlacementDates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePlacementDates(string? opensAt, string? closesAt, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var pattern = LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-ddTHH:mm");
        LocalDateTime? opens = opensAt is { Length: > 0 } ? pattern.Parse(opensAt).Value : null;
        LocalDateTime? closes = closesAt is { Length: > 0 } ? pattern.Parse(closesAt).Value : null;

        await _campMapService.UpdatePlacementDatesAsync(opens, closes, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/DeleteLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLimitZone(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        await _campMapService.DeleteLimitZoneAsync(userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/UploadOfficialZones")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadOfficialZones(IFormFile file, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        using var reader = new StreamReader(file.OpenReadStream());
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        await _campMapService.UpdateOfficialZonesAsync(geoJson, userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/DeleteOfficialZones")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOfficialZones(CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();
        await _campMapService.DeleteOfficialZonesAsync(userId, cancellationToken);
        return RedirectToAction(nameof(Admin));
    }
}
