using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("CityPlanning")]
public class CityPlanningController : HumansControllerBase
{
    private readonly ICityPlanningService _cityPlanningService;

    public CityPlanningController(ICityPlanningService cityPlanningService, UserManager<User> userManager)
        : base(userManager)
    {
        _cityPlanningService = cityPlanningService;
    }

    private async Task<bool> IsMapAdminAsync(Guid userId, CancellationToken ct)
    {
        return RoleChecks.IsCampAdmin(User) ||
               await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId, ct);
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var isMapAdmin = await IsMapAdminAsync(user.Id, cancellationToken);
        var userSeasonId = await _cityPlanningService.GetUserCampSeasonIdForYearAsync(user.Id, settings.Year, cancellationToken);
        var seasonsWithout = await _cityPlanningService.GetCampSeasonsWithoutCampPolygonAsync(settings.Year, cancellationToken);

        ViewBag.IsPlacementOpen = settings.IsPlacementOpen;
        ViewBag.IsMapAdmin = isMapAdmin;
        ViewBag.UserCampSeasonId = userSeasonId?.ToString() ?? string.Empty;
        ViewBag.CurrentUserId = user.Id.ToString();
        ViewBag.SeasonsWithoutCampPolygon = seasonsWithout;
        ViewBag.Year = settings.Year;
        ViewBag.PlacementOpensAt = settings.PlacementOpensAt;
        ViewBag.PlacementClosesAt = settings.PlacementClosesAt;

        return View();
    }

    [HttpGet("Admin")]
    public async Task<IActionResult> Admin(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        ViewBag.Settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        return View();
    }

    [HttpPost("Admin/OpenPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        await _cityPlanningService.OpenPlacementAsync(user.Id, cancellationToken);
        SetSuccess("Placement phase opened.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/ClosePlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        await _cityPlanningService.ClosePlacementAsync(user.Id, cancellationToken);
        SetSuccess("Placement phase closed.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/UploadLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLimitZone(IFormFile file, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        if (file is null || file.Length == 0)
        {
            SetError("Please select a GeoJSON file to upload.");
            return RedirectToAction(nameof(Admin));
        }
        using var reader = new StreamReader(file.OpenReadStream());
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        await _cityPlanningService.UpdateLimitZoneAsync(geoJson, user.Id, cancellationToken);
        SetSuccess("Limit zone uploaded.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/UpdatePlacementDates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePlacementDates(string? opensAt, string? closesAt, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var pattern = LocalDateTimePattern.CreateWithInvariantCulture("yyyy-MM-ddTHH:mm");

        LocalDateTime? opens = null;
        if (opensAt is { Length: > 0 })
        {
            var result = pattern.Parse(opensAt);
            if (!result.Success) { SetError("Invalid opens-at date format."); return RedirectToAction(nameof(Admin)); }
            opens = result.Value;
        }

        LocalDateTime? closes = null;
        if (closesAt is { Length: > 0 })
        {
            var result = pattern.Parse(closesAt);
            if (!result.Success) { SetError("Invalid closes-at date format."); return RedirectToAction(nameof(Admin)); }
            closes = result.Value;
        }

        await _cityPlanningService.UpdatePlacementDatesAsync(opens, closes, cancellationToken);
        SetSuccess("Placement dates updated.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpGet("Admin/DownloadLimitZone")]
    public async Task<IActionResult> DownloadLimitZone(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        if (settings.LimitZoneGeoJson is null)
            return NotFound();
        var bytes = System.Text.Encoding.UTF8.GetBytes(settings.LimitZoneGeoJson);
        return File(bytes, "application/geo+json", $"limit-zone-{settings.Year}.geojson");
    }

    [HttpPost("Admin/DeleteLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLimitZone(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        await _cityPlanningService.DeleteLimitZoneAsync(user.Id, cancellationToken);
        SetSuccess("Limit zone deleted.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("Admin/UploadOfficialZones")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadOfficialZones(IFormFile file, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        if (file is null || file.Length == 0)
        {
            SetError("Please select a GeoJSON file to upload.");
            return RedirectToAction(nameof(Admin));
        }
        using var reader = new StreamReader(file.OpenReadStream());
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        await _cityPlanningService.UpdateOfficialZonesAsync(geoJson, user.Id, cancellationToken);
        SetSuccess("Official zones uploaded.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpGet("Admin/DownloadOfficialZones")]
    public async Task<IActionResult> DownloadOfficialZones(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        if (settings.OfficialZonesGeoJson is null)
            return NotFound();
        var bytes = System.Text.Encoding.UTF8.GetBytes(settings.OfficialZonesGeoJson);
        return File(bytes, "application/geo+json", $"official-zones-{settings.Year}.geojson");
    }

    [HttpPost("Admin/DeleteOfficialZones")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOfficialZones(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();
        await _cityPlanningService.DeleteOfficialZonesAsync(user.Id, cancellationToken);
        SetSuccess("Official zones deleted.");
        return RedirectToAction(nameof(Admin));
    }
}
