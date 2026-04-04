using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Web.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Humans.Web.Controllers;

[Authorize]
[Route("api/camp-map")]
[ApiController]
public class CampMapApiController : ControllerBase
{
    private readonly ICampMapService _campMapService;
    private readonly IHubContext<CampMapHub> _hubContext;
    private readonly UserManager<User> _userManager;

    public CampMapApiController(
        ICampMapService campMapService,
        IHubContext<CampMapHub> hubContext,
        UserManager<User> userManager)
    {
        _campMapService = campMapService;
        _hubContext = hubContext;
        _userManager = userManager;
    }

    private Guid CurrentUserId() => Guid.Parse(_userManager.GetUserId(User)!);

    /// <summary>Returns current map state: settings, all camp polygons, unmapped seasons.</summary>
    [HttpGet("state")]
    public async Task<IActionResult> GetState(CancellationToken cancellationToken)
    {
        var settings = await _campMapService.GetSettingsAsync(cancellationToken);
        var campPolygons = await _campMapService.GetCampPolygonsAsync(settings.Year, cancellationToken);
        var seasonsWithout = await _campMapService.GetCampSeasonsWithoutCampPolygonAsync(settings.Year, cancellationToken);

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
        var history = await _campMapService.GetCampPolygonHistoryAsync(campSeasonId, cancellationToken);
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
        if (!await _campMapService.CanUserEditAsync(userId, campSeasonId, cancellationToken))
            return Forbid();

        var (polygon, _) = await _campMapService.SaveCampPolygonAsync(
            campSeasonId, request.GeoJson, request.AreaSqm, userId,
            cancellationToken: cancellationToken);

        var soundZone = await _campMapService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
        var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
        var campName = await _campMapService.GetCampSeasonNameAsync(campSeasonId, cancellationToken) ?? string.Empty;
        await _hubContext.Clients.All.SendAsync(
            "CampPolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, campName, cancellationToken);

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
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var (polygon, _) = await _campMapService.RestoreCampPolygonVersionAsync(
            campSeasonId, historyId, userId, cancellationToken);

        var soundZone = await _campMapService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
        var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
        var campName = await _campMapService.GetCampSeasonNameAsync(campSeasonId, cancellationToken) ?? string.Empty;
        await _hubContext.Clients.All.SendAsync(
            "CampPolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, campName, cancellationToken);

        return Ok(new { campSeasonId, geoJson = polygon.GeoJson, areaSqm = polygon.AreaSqm });
    }

    /// <summary>Export all camp polygons for a year as GeoJSON FeatureCollection. Map admins only.</summary>
    [HttpGet("export.geojson")]
    public async Task<IActionResult> ExportGeoJson([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var settings = await _campMapService.GetSettingsAsync(cancellationToken);
        var exportYear = year ?? settings.Year;
        var geoJson = await _campMapService.ExportAsGeoJsonAsync(exportYear, cancellationToken);

        return Content(geoJson, "application/geo+json");
    }
}
