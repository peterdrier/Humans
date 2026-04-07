using System.Text.Json;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Humans.Web.Controllers;

[Authorize]
[Route("api/city-planning")]
[ApiController]
public class CityPlanningApiController : ControllerBase
{
    private readonly ICityPlanningService _cityPlanningService;
    private readonly IHubContext<CityPlanningHub> _hubContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<CityPlanningApiController> _logger;

    public CityPlanningApiController(
        ICityPlanningService cityPlanningService,
        IHubContext<CityPlanningHub> hubContext,
        UserManager<User> userManager,
        ILogger<CityPlanningApiController> logger)
    {
        _cityPlanningService = cityPlanningService;
        _hubContext = hubContext;
        _userManager = userManager;
        _logger = logger;
    }

    private Guid CurrentUserId()
    {
        var id = _userManager.GetUserId(User)
                 ?? throw new InvalidOperationException("Authenticated user has no ID claim.");
        return Guid.Parse(id);
    }

    private async Task<bool> IsMapAdminAsync(Guid userId, CancellationToken ct)
    {
        return RoleChecks.IsCampAdmin(User) ||
               await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId, ct);
    }

    /// <summary>Returns current map state: settings, all camp polygons, unmapped seasons.</summary>
    [HttpGet("state")]
    public async Task<IActionResult> GetState(CancellationToken cancellationToken)
    {
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var campPolygons = await _cityPlanningService.GetCampPolygonsAsync(settings.Year, cancellationToken);
        var seasonsWithout = await _cityPlanningService.GetCampSeasonsWithoutCampPolygonAsync(settings.Year, cancellationToken);

        return Ok(new
        {
            isPlacementOpen = settings.IsPlacementOpen,
            limitZoneGeoJson = settings.LimitZoneGeoJson,
            officialZonesGeoJson = settings.OfficialZonesGeoJson,
            campPolygons,
            campSeasonsWithoutPolygon = seasonsWithout
        });
    }

    /// <summary>Returns camp polygon version history for a camp season, newest first.</summary>
    [HttpGet("camp-polygons/{campSeasonId:guid}/history")]
    public async Task<IActionResult> GetCampPolygonHistory(Guid campSeasonId, CancellationToken cancellationToken)
    {
        var history = await _cityPlanningService.GetCampPolygonHistoryAsync(campSeasonId, cancellationToken);
        return Ok(history);
    }

    /// <summary>Save or update a camp polygon. Broadcasts update to all connected clients via SignalR.</summary>
    [HttpPut("camp-polygons/{campSeasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCampPolygon(
        Guid campSeasonId,
        [FromBody] SaveCampPolygonRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!RoleChecks.IsCampAdmin(User) &&
            !await _cityPlanningService.CanUserEditAsync(userId, campSeasonId, cancellationToken))
            return Forbid();

        if (string.IsNullOrWhiteSpace(request.GeoJson) || !IsValidJson(request.GeoJson))
            return BadRequest("Invalid GeoJSON.");

        var (polygon, _) = await _cityPlanningService.SaveCampPolygonAsync(
            campSeasonId, request.GeoJson, request.AreaSqm, userId,
            cancellationToken: cancellationToken);

        var soundZone = await _cityPlanningService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
        var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
        var campName = await _cityPlanningService.GetCampSeasonNameAsync(campSeasonId, cancellationToken) ?? string.Empty;
        try
        {
            await _hubContext.Clients.All.SendAsync(
                "CampPolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, campName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast CampPolygonUpdated for {CampSeasonId}", campSeasonId);
        }

        return Ok(new { campSeasonId, geoJson = polygon.GeoJson, areaSqm = polygon.AreaSqm });
    }

    /// <summary>Restore a camp polygon to a historical version. Map admins only.</summary>
    [HttpPost("camp-polygons/{campSeasonId:guid}/restore/{historyId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestoreCampPolygon(
        Guid campSeasonId,
        Guid historyId,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await IsMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var (polygon, _) = await _cityPlanningService.RestoreCampPolygonVersionAsync(
            campSeasonId, historyId, userId, cancellationToken);

        var soundZone = await _cityPlanningService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
        var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
        var campName = await _cityPlanningService.GetCampSeasonNameAsync(campSeasonId, cancellationToken) ?? string.Empty;
        try
        {
            await _hubContext.Clients.All.SendAsync(
                "CampPolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, campName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast CampPolygonUpdated for {CampSeasonId}", campSeasonId);
        }

        return Ok(new { campSeasonId, geoJson = polygon.GeoJson, areaSqm = polygon.AreaSqm });
    }

    /// <summary>Export all camp polygons for a year as GeoJSON FeatureCollection. Map admins only.</summary>
    [HttpGet("export.geojson")]
    public async Task<IActionResult> ExportGeoJson([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await IsMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var exportYear = year ?? settings.Year;
        var geoJson = await _cityPlanningService.ExportAsGeoJsonAsync(exportYear, cancellationToken);

        return Content(geoJson, "application/geo+json");
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            JsonDocument.Parse(value).Dispose();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
