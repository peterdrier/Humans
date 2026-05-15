using System.Text.Json;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Authorization.Requirements;
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
    private readonly IAuthorizationService _authorizationService;
    private readonly IHubContext<CityPlanningHub> _hubContext;
    private readonly UserManager<User> _userManager;
    private readonly ILogger<CityPlanningApiController> _logger;

    public CityPlanningApiController(
        ICityPlanningService cityPlanningService,
        ICampService campService,
        IContainerService containerService,
        IAuthorizationService authorizationService,
        IHubContext<CityPlanningHub> hubContext,
        UserManager<User> userManager,
        ILogger<CityPlanningApiController> logger)
    {
        _cityPlanningService = cityPlanningService;
        _campService = campService;
        _containerService = containerService;
        _authorizationService = authorizationService;
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

    private async Task<Guid?> FindUserLeadCampIdAsync(Guid userId, int year, CancellationToken ct)
    {
        var campsWithLeads = await _campService.GetCampsWithLeadsForYearAsync(year, cancellationToken: ct);
        return campsWithLeads
            .FirstOrDefault(c => c.Leads?.Any(l => l.UserId == userId) == true)?.Id;
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

    /// <summary>Returns all containers and their placements for a year, with canEdit flag per caller.</summary>
    [HttpGet("containers/{year:int}")]
    public async Task<IActionResult> GetContainers(int year, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var isMapAdmin = await IsMapAdminAsync(userId, cancellationToken);
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var userCampId = await FindUserLeadCampIdAsync(userId, year, cancellationToken);

        var containers = await _containerService.GetAllAsync(cancellationToken);
        var placements = await _containerService.GetPlacementsByYearAsync(year, cancellationToken);
        var placementByContainerId = placements.ToDictionary(p => p.ContainerId, p => p);
        var camps = await _campService.GetCampsForYearAsync(year, cancellationToken);
        var campNameById = camps.ToDictionary(c => c.Id, c => c.Seasons.First(s => s.Year == year).Name);

        var result = containers.Select(c =>
        {
            placementByContainerId.TryGetValue(c.Id, out var placement);
            return new ContainerWithPlacementApiDto(
                c.Id,
                c.Name,
                c.Description,
                c.CampId,
                campNameById.GetValueOrDefault(c.CampId) ?? string.Empty,
                placement?.LocationGeoJson,
                placement?.PlacementNotes,
                placement?.PlacementImageStoragePath,
                placement?.PlacementImageFileName,
                isMapAdmin ||
                    (settings.IsContainerPlacementOpen &&
                     userCampId.HasValue &&
                     c.CampId == userCampId));
        });

        return Ok(result);
    }

    /// <summary>Export placed containers accessible to the caller as a GeoJSON FeatureCollection.</summary>
    [HttpGet("containers/{year:int}/export.geojson")]
    public async Task<IActionResult> ExportContainersGeoJson(int year, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var isMapAdmin = await IsMapAdminAsync(userId, cancellationToken);
        var userCampId = await FindUserLeadCampIdAsync(userId, year, cancellationToken);

        if (!isMapAdmin && !userCampId.HasValue)
        {
            return Forbid();
        }

        var allContainers = await _containerService.GetAllAsync(cancellationToken);
        var placements = await _containerService.GetPlacementsByYearAsync(year, cancellationToken);
        var containersById = allContainers.ToDictionary(c => c.Id, c => c);

        var placed = placements
            .Where(p => p.LocationGeoJson is not null)
            .Where(p => containersById.ContainsKey(p.ContainerId))
            .Select(p => (Placement: p, Container: containersById[p.ContainerId]))
            .Where(t => isMapAdmin || t.Container.CampId == userCampId)
            .ToList();

        var features = placed.Select(t =>
        {
            var f = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(t.Placement.LocationGeoJson!);
            var props = f?["properties"]?.AsObject();
            if (props != null)
            {
                props["containerId"] = t.Container.Id.ToString();
                props["containerName"] = t.Container.Name;
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

    /// <summary>Save or update the placement GeoJSON for a container in the given year.</summary>
    [HttpPut("containers/{id:guid}/placement/{year:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveContainerPlacement(
        Guid id,
        int year,
        [FromBody] SaveContainerPlacementRequest request,
        CancellationToken cancellationToken)
    {
        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var authResult = await _authorizationService.AuthorizeAsync(
            User, ContainerAuthorizationTarget.For(container), ContainerOperationRequirement.Place);
        if (!authResult.Succeeded) return Forbid();

        if (string.IsNullOrWhiteSpace(request.GeoJson) || !IsValidContainerPlacementGeoJson(request.GeoJson))
        {
            return UnprocessableEntity("Invalid container placement GeoJSON.");
        }

        var updated = await _containerService.SavePlacementAsync(id, year, request.GeoJson, CurrentUserId(), cancellationToken);
        return Ok(new { id = updated.ContainerId, year = updated.Year, locationGeoJson = updated.LocationGeoJson });
    }

    /// <summary>Update placement notes and/or sketch image for a placed container.</summary>
    [HttpPut("containers/{id:guid}/placement/{year:int}/notes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateContainerPlacementNotes(
        Guid id,
        int year,
        [FromForm] UpdateContainerPlacementNotesRequest request,
        CancellationToken cancellationToken)
    {
        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var authResult = await _authorizationService.AuthorizeAsync(
            User, ContainerAuthorizationTarget.For(container), ContainerOperationRequirement.Place);
        if (!authResult.Succeeded) return Forbid();

        ContainerImageUpload? imageUpload = null;
        if (request.PlacementImage is { Length: > 0 } f)
        {
            imageUpload = new ContainerImageUpload(f.OpenReadStream(), f.ContentType, f.FileName, f.Length);
        }

        try
        {
            var updated = await _containerService.UpdatePlacementNotesAsync(
                id, year, request.PlacementNotes, imageUpload, request.RemovePlacementImage,
                CurrentUserId(), cancellationToken);
            return Ok(new
            {
                id = updated.ContainerId,
                year = updated.Year,
                placementNotes = updated.PlacementNotes,
                placementImageUrl = updated.PlacementImageStoragePath,
                placementImageFileName = updated.PlacementImageFileName,
            });
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(ex.Message);
        }
    }

    /// <summary>Clear the placement GeoJSON for a container in the given year.</summary>
    [HttpDelete("containers/{id:guid}/placement/{year:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearContainerPlacement(Guid id, int year, CancellationToken cancellationToken)
    {
        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var authResult = await _authorizationService.AuthorizeAsync(
            User, ContainerAuthorizationTarget.For(container), ContainerOperationRequirement.Place);
        if (!authResult.Succeeded) return Forbid();

        await _containerService.ClearPlacementAsync(id, year, CurrentUserId(), cancellationToken);
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

public record ContainerWithPlacementApiDto(
    Guid Id,
    string Name,
    string? Description,
    Guid CampId,
    string CampName,
    string? LocationGeoJson,
    string? PlacementNotes,
    string? PlacementImageUrl,
    string? PlacementImageFileName,
    bool CanEdit);

public class UpdateContainerPlacementNotesRequest
{
    public string? PlacementNotes { get; set; }
    public IFormFile? PlacementImage { get; set; }
    public bool RemovePlacementImage { get; set; }
}
