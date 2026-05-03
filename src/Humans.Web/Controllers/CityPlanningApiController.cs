using System.Text.Json;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
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
    private readonly ICampService _campService;
    private readonly IContainerService _containerService;
    private readonly IHubContext<CityPlanningHub> _hubContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<CityPlanningApiController> _logger;

    public CityPlanningApiController(
        ICityPlanningService cityPlanningService,
        ICampService campService,
        IContainerService containerService,
        IHubContext<CityPlanningHub> hubContext,
        UserManager<User> userManager,
        ILogger<CityPlanningApiController> logger)
    {
        _cityPlanningService = cityPlanningService;
        _campService = campService;
        _containerService = containerService;
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
        var response = history.Select(h => new
        {
            id = h.Id,
            modifiedByDisplayName = h.ModifiedByDisplayName,
            modifiedAt = h.ModifiedAt.ToDisplayDateTime(),
            areaSqm = h.AreaSqm,
            note = h.Note,
            geoJson = h.GeoJson,
        });
        return Ok(response);
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
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.GeoJson) || !IsValidJson(request.GeoJson))
        {
            return BadRequest("Invalid GeoJSON.");
        }

        var (polygon, _) = await _cityPlanningService.SaveCampPolygonAsync(
            campSeasonId, request.GeoJson, request.AreaSqm, userId,
            note: request.Note ?? "Saved",
            cancellationToken: cancellationToken);

        var season = await _campService.GetCampSeasonByIdAsync(campSeasonId, cancellationToken);
        var soundZoneValue = season?.SoundZone is { } sz ? (int)sz : -1;
        var campName = season?.Name ?? string.Empty;
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
        {
            return Forbid();
        }

        var (polygon, _) = await _cityPlanningService.RestoreCampPolygonVersionAsync(
            campSeasonId, historyId, userId, cancellationToken);

        var season = await _campService.GetCampSeasonByIdAsync(campSeasonId, cancellationToken);
        var soundZoneValue = season?.SoundZone is { } sz ? (int)sz : -1;
        var campName = season?.Name ?? string.Empty;
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
        {
            return Forbid();
        }

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var exportYear = year ?? settings.Year;
        var geoJson = await _cityPlanningService.ExportAsGeoJsonAsync(exportYear, cancellationToken);

        return Content(geoJson, "application/geo+json");
    }

    /// <summary>Returns all containers for a year with canEdit flag per caller.</summary>
    [HttpGet("containers/{year:int}")]
    public async Task<IActionResult> GetContainers(int year, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var isMapAdmin = await IsMapAdminAsync(userId, cancellationToken);
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(userId, year, cancellationToken);

        var containers = await _containerService.GetAllByYearAsync(year, cancellationToken);

        var result = containers.Select(c => new ContainerPlacementDto(
            c.Id,
            c.Name,
            c.Description,
            c.CampSeasonId,
            c.LocationGeoJson,
            isMapAdmin ||
                (settings.IsContainerPlacementOpen &&
                 userSeasonId.HasValue &&
                 c.CampSeasonId == userSeasonId)));

        return Ok(result);
    }

    /// <summary>Export placed containers accessible to the caller as a GeoJSON FeatureCollection.</summary>
    [HttpGet("containers/{year:int}/export.geojson")]
    public async Task<IActionResult> ExportContainersGeoJson(int year, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var isMapAdmin = await IsMapAdminAsync(userId, cancellationToken);
        var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(userId, year, cancellationToken);

        if (!isMapAdmin && !userSeasonId.HasValue)
        {
            return Forbid();
        }

        var allContainers = await _containerService.GetAllByYearAsync(year, cancellationToken);

        var placed = allContainers
            .Where(c => c.LocationGeoJson is not null)
            .Where(c => isMapAdmin || c.CampSeasonId == userSeasonId)
            .ToList();

        var features = placed.Select(c =>
        {
            var f = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(c.LocationGeoJson!);
            var props = f?["properties"]?.AsObject();
            if (props != null)
            {
                props["containerId"] = c.Id.ToString();
                props["containerName"] = c.Name;
                props["type"] = "Container";
            }
            return f;
        });

        var collection = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "FeatureCollection",
            ["features"] = new System.Text.Json.Nodes.JsonArray(features.Select(f => (System.Text.Json.Nodes.JsonNode?)f).ToArray()),
        };

        var fileName = $"containers-{year}.geojson";
        return File(System.Text.Encoding.UTF8.GetBytes(collection.ToJsonString()), "application/geo+json", fileName);
    }

    /// <summary>Save or update the placement GeoJSON for a container.</summary>
    [HttpPut("containers/{id:guid}/placement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContainerPlacement(
        Guid id,
        [FromBody] SaveContainerPlacementRequest request,
        CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var isMapAdmin = await IsMapAdminAsync(userId, cancellationToken);
        if (!isMapAdmin)
        {
            var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
            var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(userId, container.Year, cancellationToken);
            if (!settings.IsContainerPlacementOpen ||
                !userSeasonId.HasValue ||
                container.CampSeasonId != userSeasonId)
                return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.GeoJson) || !IsValidContainerPlacementGeoJson(request.GeoJson))
        {
            return UnprocessableEntity("Invalid container placement GeoJSON.");
        }

        var updated = await _containerService.SavePlacementAsync(id, request.GeoJson, cancellationToken);
        return Ok(new { id = updated.Id, locationGeoJson = updated.LocationGeoJson });
    }

    /// <summary>Clear the placement GeoJSON for a container.</summary>
    [HttpDelete("containers/{id:guid}/placement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearContainerPlacement(Guid id, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var isMapAdmin = await IsMapAdminAsync(userId, cancellationToken);
        if (!isMapAdmin)
        {
            var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
            var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(userId, container.Year, cancellationToken);
            if (!settings.IsContainerPlacementOpen ||
                !userSeasonId.HasValue ||
                container.CampSeasonId != userSeasonId)
                return Forbid();
        }

        await _containerService.ClearPlacementAsync(id, cancellationToken);
        return NoContent();
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

    private static bool IsValidContainerPlacementGeoJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "Feature", StringComparison.Ordinal)) return false;
            var geom = root.GetProperty("geometry");
            if (!string.Equals(geom.GetProperty("type").GetString(), "Polygon", StringComparison.Ordinal)) return false;
            var props = root.GetProperty("properties");
            if (!props.TryGetProperty("center_lng", out _)) return false;
            if (!props.TryGetProperty("center_lat", out _)) return false;
            if (!props.TryGetProperty("rotation_degrees", out _)) return false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}

public record SaveContainerPlacementRequest(string GeoJson);

public record ContainerPlacementDto(
    Guid Id,
    string Name,
    string? Description,
    Guid? CampSeasonId,
    string? LocationGeoJson,
    bool CanEdit);
