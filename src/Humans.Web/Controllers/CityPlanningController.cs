using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NodaTime.Text;

using Humans.Application;
using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize]
[Route("CityPlanning")]
public class CityPlanningController : HumansControllerBase
{
    private readonly ICityPlanningService _cityPlanningService;
    private readonly ICampService _campService;
    private readonly IContainerService _containerService;
    private readonly ILogger<CityPlanningController> _logger;

    public CityPlanningController(
        ICityPlanningService cityPlanningService,
        ICampService campService,
        IContainerService containerService,
        IUserService userService,
        ILogger<CityPlanningController> logger)
        : base(userService)
    {
        _cityPlanningService = cityPlanningService;
        _campService = campService;
        _containerService = containerService;
        _logger = logger;
    }

    private async Task<bool> IsMapAdminAsync(Guid userId, CancellationToken ct)
    {
        return RoleChecks.IsCampAdmin(User) ||
               await _cityPlanningService.IsCityPlanningTeamMemberAsync(userId, ct);
    }

    /// <summary>Resolves the current user and gates on the map-admin check.</summary>
    private async Task<(IActionResult? Error, UserInfo? User)> RequireMapAdminAsync(CancellationToken ct)
    {
        var (userError, user) = await RequireCurrentUserAsync();
        if (userError is not null) return (userError, null);
        if (!await IsMapAdminAsync(user.Id, ct)) return (Forbid(), null);
        return (null, user);
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var isMapAdmin = await IsMapAdminAsync(user.Id, cancellationToken);
        var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(user.Id, settings.Year, cancellationToken);

        return View(new CityPlanningIndexViewModel
        {
            Year = settings.Year,
            IsMapAdmin = isMapAdmin,
            IsBarrioLead = userSeasonId.HasValue,
            IsPlacementOpen = settings.IsPlacementOpen,
            IsContainerPlacementOpen = settings.IsContainerPlacementOpen,
        });
    }

    [HttpGet("BarrioMap")]
    public async Task<IActionResult> BarrioMap(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var isMapAdmin = await IsMapAdminAsync(user.Id, cancellationToken);
        var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(user.Id, settings.Year, cancellationToken);
        var seasonsWithout = await _cityPlanningService.GetCampSeasonsWithoutCampPolygonAsync(settings.Year, cancellationToken);

        return View(new CityPlanningBarrioMapViewModel
        {
            Year = settings.Year,
            IsPlacementOpen = settings.IsPlacementOpen,
            IsMapAdmin = isMapAdmin,
            UserCampSeasonId = userSeasonId?.ToString() ?? string.Empty,
            CurrentUserId = user.Id,
            SeasonsWithoutCampPolygon = seasonsWithout.ToList(),
            PlacementOpensAt = settings.PlacementOpensAt,
            PlacementClosesAt = settings.PlacementClosesAt,
        });
    }

    [HttpGet("BarrioMap/Admin")]
    public async Task<IActionResult> Admin(CancellationToken cancellationToken)
    {
        var (error, _) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        ViewBag.Settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        return View();
    }

    [HttpPost("BarrioMap/Admin/OpenPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        await _cityPlanningService.OpenPlacementAsync(user!.Id, cancellationToken);
        SetSuccess("Placement phase opened.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("BarrioMap/Admin/ClosePlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        await _cityPlanningService.ClosePlacementAsync(user!.Id, cancellationToken);
        SetSuccess("Placement phase closed.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("BarrioMap/Admin/OpenContainerPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenContainerPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        await _cityPlanningService.OpenContainerPlacementAsync(user!.Id, cancellationToken);
        SetSuccess("Container placement phase opened.");
        return RedirectToAction(nameof(Containers), new { year = settings.Year });
    }

    [HttpPost("BarrioMap/Admin/CloseContainerPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseContainerPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        await _cityPlanningService.CloseContainerPlacementAsync(user!.Id, cancellationToken);
        SetSuccess("Container placement phase closed.");
        return RedirectToAction(nameof(Containers), new { year = settings.Year });
    }

    [HttpPost("BarrioMap/Admin/UploadLimitZone")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UploadLimitZone(IFormFile? file, CancellationToken cancellationToken) =>
        UploadGeoJsonAsync(file, "Limit zone", _cityPlanningService.UpdateLimitZoneAsync, cancellationToken);

    [HttpPost("BarrioMap/Admin/UploadOfficialZones")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> UploadOfficialZones(IFormFile? file, CancellationToken cancellationToken) =>
        UploadGeoJsonAsync(file, "Official zones", _cityPlanningService.UpdateOfficialZonesAsync, cancellationToken);

    [HttpGet("BarrioMap/Admin/DownloadLimitZone")]
    public Task<IActionResult> DownloadLimitZone(CancellationToken cancellationToken) =>
        DownloadGeoJsonAsync(s => s.LimitZoneGeoJson, "limit-zone", cancellationToken);

    [HttpGet("BarrioMap/Admin/DownloadOfficialZones")]
    public Task<IActionResult> DownloadOfficialZones(CancellationToken cancellationToken) =>
        DownloadGeoJsonAsync(s => s.OfficialZonesGeoJson, "official-zones", cancellationToken);

    [HttpPost("BarrioMap/Admin/DeleteLimitZone")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteLimitZone(CancellationToken cancellationToken) =>
        DeleteAdminResourceAsync("Limit zone", _cityPlanningService.DeleteLimitZoneAsync, cancellationToken);

    [HttpPost("BarrioMap/Admin/DeleteOfficialZones")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteOfficialZones(CancellationToken cancellationToken) =>
        DeleteAdminResourceAsync("Official zones", _cityPlanningService.DeleteOfficialZonesAsync, cancellationToken);

    [HttpPost("BarrioMap/Admin/UpdatePlacementDates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePlacementDates(string? opensAt, string? closesAt, CancellationToken cancellationToken)
    {
        var (error, _) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

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

    /// <summary>Shared GeoJSON upload (LimitZone / OfficialZones); callers differ only by label and service method.</summary>
    private async Task<IActionResult> UploadGeoJsonAsync(
        IFormFile? file,
        string namePretty,
        Func<string, Guid, CancellationToken, Task> updateAsync,
        CancellationToken ct)
    {
        var (error, user) = await RequireMapAdminAsync(ct);
        if (error is not null) return error;

        if (file is null || file.Length == 0)
        {
            SetError("Please select a GeoJSON file to upload.");
            return RedirectToAction(nameof(Admin));
        }
        if (file.Length > 10 * 1024 * 1024)
        {
            SetError("File too large. Maximum size is 10 MB.");
            return RedirectToAction(nameof(Admin));
        }
        using var reader = new StreamReader(file.OpenReadStream());
        var geoJson = await reader.ReadToEndAsync(ct);
        if (!IsValidJson(geoJson))
        {
            SetError("Invalid GeoJSON file. Please upload a valid JSON file.");
            return RedirectToAction(nameof(Admin));
        }
        await updateAsync(geoJson, user!.Id, ct);
        SetSuccess($"{namePretty} uploaded.");
        return RedirectToAction(nameof(Admin));
    }

    private async Task<IActionResult> DownloadGeoJsonAsync(
        Func<CityPlanningSettingsDto, string?> selector,
        string filenamePrefix,
        CancellationToken ct)
    {
        var (error, _) = await RequireMapAdminAsync(ct);
        if (error is not null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(ct);
        var content = selector(settings);
        if (content is null) return NotFound();

        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return File(bytes, "application/geo+json", $"{filenamePrefix}-{settings.Year}.geojson");
    }

    private async Task<IActionResult> DeleteAdminResourceAsync(
        string namePretty,
        Func<Guid, CancellationToken, Task> deleteAsync,
        CancellationToken ct)
    {
        var (error, user) = await RequireMapAdminAsync(ct);
        if (error is not null) return error;

        await deleteAsync(user!.Id, ct);
        SetSuccess($"{namePretty} deleted.");
        return RedirectToAction(nameof(Admin));
    }

    // --- Containers ---

    [HttpGet("ContainerMap/{year:int}")]
    public async Task<IActionResult> ContainerMap(int year, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        var isMapAdmin = await IsMapAdminAsync(user.Id, cancellationToken);
        var userCamp = await FindUserLeadCampAsync(user.Id, year, cancellationToken);
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);

        if (!isMapAdmin && (!settings.IsContainerPlacementOpen || userCamp is null))
        {
            return Forbid();
        }

        var (campSlug, campName) = LeadCampDisplay(isMapAdmin, userCamp, year);

        return View(new ContainerMapViewModel
        {
            Year = year,
            IsMapAdmin = isMapAdmin,
            UserCampId = userCamp?.Id.ToString() ?? string.Empty,
            CampSlug = campSlug,
            CampName = campName,
        });
    }

    private async Task<CampInfo?> FindUserLeadCampAsync(Guid userId, int year, CancellationToken ct)
    {
        var camps = await _campService.GetCampsForYearAsync(year, ct);
        return camps.FirstOrDefault(c =>
            c.Leads.Any(l => l.UserId == userId));
    }

    private static (string Slug, string Name) LeadCampDisplay(bool isMapAdmin, CampInfo? userCamp, int year)
    {
        if (isMapAdmin || userCamp is null) return (string.Empty, string.Empty);
        var season = userCamp.Seasons.First(s => s.Year == year);
        return (userCamp.Slug, season.Name);
    }

    [HttpGet("BarrioMap/Admin/Containers/{year:int}")]
    public async Task<IActionResult> Containers(int year, CancellationToken cancellationToken)
    {
        var (error, _) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var overview = await _containerService.GetAdminOverviewAsync(year, cancellationToken);

        var vm = new OrgContainerIndexViewModel
        {
            Year = year,
            IsContainerPlacementOpen = settings.IsContainerPlacementOpen,
            BarrioGroups = overview.CampGroups
                .OrderBy(g => g.CampName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new BarrioContainerGroup
                {
                    CampId = g.CampId,
                    CampName = g.CampName,
                    CampSlug = g.CampSlug,
                    Containers = g.Containers
                        .OrderBy(c => c.Container.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(ToContainerWithPlacementViewModel)
                        .ToList()
                })
                .ToList()
        };

        return View(vm);
    }

    private static ContainerViewModel ToContainerViewModel(ContainerDto c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description,
        ImageUrl = c.ImageStoragePath,
        ImageFileName = c.ImageFileName,
    };

    private static ContainerPlacementViewModel? ToPlacementViewModel(ContainerPlacementDto? p) =>
        p is null ? null : new ContainerPlacementViewModel
        {
            ContainerId = p.ContainerId,
            Year = p.Year,
            LocationGeoJson = p.LocationGeoJson,
            PlacementNotes = p.PlacementNotes,
            PlacementImageUrl = p.PlacementImageStoragePath,
            PlacementImageFileName = p.PlacementImageFileName,
        };

    private static ContainerWithPlacementViewModel ToContainerWithPlacementViewModel(ContainerWithPlacement cwp) => new()
    {
        Container = ToContainerViewModel(cwp.Container),
        Placement = ToPlacementViewModel(cwp.Placement),
    };

    [HttpPost("BarrioMap/Admin/Containers/Barrios/{campId}/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBarrioContainer(Guid campId, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Containers), new { year = settings.Year });
        }

        return await TryCreateContainerAsync(model, campId, settings.Year, user!.Id, cancellationToken);
    }

    private async Task<IActionResult> TryCreateContainerAsync(
        ContainerFormModel model, Guid campId, int year, Guid actorUserId, CancellationToken ct)
    {
        try
        {
            await _containerService.CreateAsync(model.ToContainerData(campId), actorUserId, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Container create failed for camp {CampId}, year {Year}: {Message}", campId, year, ex.Message);
            SetError(ex.Message);
            return RedirectToAction(nameof(Containers), new { year });
        }

        SetSuccess("Container added.");
        return RedirectToAction(nameof(Containers), new { year });
    }

    [HttpPost("BarrioMap/Admin/Containers/{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditContainer(Guid id, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Containers), new { year = settings.Year });
        }

        return await TryUpdateContainerAsync(id, model, container.CampId, settings.Year, user!.Id, cancellationToken);
    }

    private async Task<IActionResult> TryUpdateContainerAsync(
        Guid id, ContainerFormModel model, Guid campId, int year, Guid actorUserId, CancellationToken ct)
    {
        try
        {
            await _containerService.UpdateAsync(id, model.ToContainerData(campId), actorUserId, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Container update failed for id {ContainerId}, camp {CampId}, year {Year}: {Message}", id, campId, year, ex.Message);
            SetError(ex.Message);
            return RedirectToAction(nameof(Containers), new { year });
        }

        SetSuccess("Container updated.");
        return RedirectToAction(nameof(Containers), new { year });
    }

    [HttpPost("BarrioMap/Admin/Containers/{id}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteContainer(Guid id, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireMapAdminAsync(cancellationToken);
        if (error is not null) return error;

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        await _containerService.DeleteAsync(id, user!.Id, cancellationToken);
        SetSuccess("Container deleted.");
        return RedirectToAction(nameof(Containers), new { year = settings.Year });
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            System.Text.Json.JsonDocument.Parse(value).Dispose();
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
