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

    /// <summary>Returns current map state: settings, all polygons, unmapped seasons.</summary>
    [HttpGet("state")]
    public async Task<IActionResult> GetState(CancellationToken cancellationToken)
    {
        var settings = await _campMapService.GetSettingsAsync(cancellationToken);
        var polygons = await _campMapService.GetPolygonsAsync(settings.Year, cancellationToken);
        var seasonsWithout = await _campMapService.GetCampSeasonsWithoutPolygonAsync(settings.Year, cancellationToken);

        return Ok(new
        {
            isPlacementOpen = settings.IsPlacementOpen,
            limitZoneGeoJson = settings.LimitZoneGeoJson,
            polygons,
            seasonsWithoutPolygon = seasonsWithout
        });
    }

    /// <summary>Returns polygon version history for a camp season, newest first.</summary>
    [HttpGet("polygons/{campSeasonId:guid}/history")]
    public async Task<IActionResult> GetHistory(Guid campSeasonId, CancellationToken cancellationToken)
    {
        var history = await _campMapService.GetPolygonHistoryAsync(campSeasonId, cancellationToken);
        return Ok(history);
    }

    /// <summary>Save or update a polygon. Broadcasts update to all connected clients via SignalR.</summary>
    [HttpPut("polygons/{campSeasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePolygon(
        Guid campSeasonId,
        [FromBody] SavePolygonRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.CanUserEditAsync(userId, campSeasonId, cancellationToken))
            return Forbid();

        var (polygon, _) = await _campMapService.SavePolygonAsync(
            campSeasonId, request.GeoJson, request.AreaSqm, userId,
            cancellationToken: cancellationToken);

        var soundZone = await _campMapService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
        var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
        var campName = await _campMapService.GetCampSeasonNameAsync(campSeasonId, cancellationToken) ?? string.Empty;
        await _hubContext.Clients.All.SendAsync(
            "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, campName, cancellationToken);

        return Ok(new { campSeasonId, geoJson = polygon.GeoJson, areaSqm = polygon.AreaSqm });
    }

    /// <summary>Restore a polygon to a historical version. Map admins only.</summary>
    [HttpPost("polygons/{campSeasonId:guid}/restore/{historyId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestorePolygon(
        Guid campSeasonId,
        Guid historyId,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        if (!await _campMapService.IsUserMapAdminAsync(userId, cancellationToken))
            return Forbid();

        var (polygon, _) = await _campMapService.RestorePolygonVersionAsync(
            campSeasonId, historyId, userId, cancellationToken);

        var soundZone = await _campMapService.GetCampSeasonSoundZoneAsync(campSeasonId, cancellationToken);
        var soundZoneValue = soundZone.HasValue ? (int)soundZone.Value : -1;
        var campName = await _campMapService.GetCampSeasonNameAsync(campSeasonId, cancellationToken) ?? string.Empty;
        await _hubContext.Clients.All.SendAsync(
            "PolygonUpdated", campSeasonId, polygon.GeoJson, polygon.AreaSqm, soundZoneValue, campName, cancellationToken);

        return Ok(new { campSeasonId, geoJson = polygon.GeoJson, areaSqm = polygon.AreaSqm });
    }

    /// <summary>Export all polygons for a year as GeoJSON FeatureCollection. Map admins only.</summary>
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
