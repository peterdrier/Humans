using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CitiPlanning;
using Humans.Application.Interfaces.Containers;
using Humans.Domain.Entities;
using Humans.Web.Authorization;
using Humans.Web.Models;
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
    private readonly ICampService _campService;
    private readonly IContainerService _containerService;

    public CityPlanningController(
        ICityPlanningService cityPlanningService,
        ICampService campService,
        IContainerService containerService,
        UserManager<User> userManager)
        : base(userManager)
    {
        _cityPlanningService = cityPlanningService;
        _campService = campService;
        _containerService = containerService;
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
        var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(user.Id, settings.Year, cancellationToken);

        ViewBag.Year = settings.Year;
        ViewBag.IsMapAdmin = isMapAdmin;
        ViewBag.IsBarrioLead = userSeasonId.HasValue;
        ViewBag.IsPlacementOpen = settings.IsPlacementOpen;
        ViewBag.IsContainerPlacementOpen = settings.IsContainerPlacementOpen;

        return View();
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

    [HttpGet("BarrioMap/Admin")]
    public async Task<IActionResult> Admin(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        ViewBag.Settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        return View();
    }

    [HttpPost("BarrioMap/Admin/OpenPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
        await _cityPlanningService.OpenPlacementAsync(user.Id, cancellationToken);
        SetSuccess("Placement phase opened.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("BarrioMap/Admin/ClosePlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClosePlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
        await _cityPlanningService.ClosePlacementAsync(user.Id, cancellationToken);
        SetSuccess("Placement phase closed.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("BarrioMap/Admin/OpenContainerPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenContainerPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        await _cityPlanningService.OpenContainerPlacementAsync(user.Id, cancellationToken);
        SetSuccess("Container placement phase opened.");
        return RedirectToAction(nameof(Containers), new { year = settings.Year });
    }

    [HttpPost("BarrioMap/Admin/CloseContainerPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseContainerPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        await _cityPlanningService.CloseContainerPlacementAsync(user.Id, cancellationToken);
        SetSuccess("Container placement phase closed.");
        return RedirectToAction(nameof(Containers), new { year = settings.Year });
    }

    [HttpPost("BarrioMap/Admin/UploadLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadLimitZone(IFormFile file, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
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
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        if (!IsValidJson(geoJson))
        {
            SetError("Invalid GeoJSON file. Please upload a valid JSON file.");
            return RedirectToAction(nameof(Admin));
        }
        await _cityPlanningService.UpdateLimitZoneAsync(geoJson, user.Id, cancellationToken);
        SetSuccess("Limit zone uploaded.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("BarrioMap/Admin/UpdatePlacementDates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePlacementDates(string? opensAt, string? closesAt, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

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

    [HttpGet("BarrioMap/Admin/DownloadLimitZone")]
    public async Task<IActionResult> DownloadLimitZone(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        if (settings.LimitZoneGeoJson is null)
        {
            return NotFound();
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(settings.LimitZoneGeoJson);
        return File(bytes, "application/geo+json", $"limit-zone-{settings.Year}.geojson");
    }

    [HttpPost("BarrioMap/Admin/DeleteLimitZone")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLimitZone(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
        await _cityPlanningService.DeleteLimitZoneAsync(user.Id, cancellationToken);
        SetSuccess("Limit zone deleted.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost("BarrioMap/Admin/UploadOfficialZones")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadOfficialZones(IFormFile file, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
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
        var geoJson = await reader.ReadToEndAsync(cancellationToken);
        if (!IsValidJson(geoJson))
        {
            SetError("Invalid GeoJSON file. Please upload a valid JSON file.");
            return RedirectToAction(nameof(Admin));
        }
        await _cityPlanningService.UpdateOfficialZonesAsync(geoJson, user.Id, cancellationToken);
        SetSuccess("Official zones uploaded.");
        return RedirectToAction(nameof(Admin));
    }

    [HttpGet("BarrioMap/Admin/DownloadOfficialZones")]
    public async Task<IActionResult> DownloadOfficialZones(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        if (settings.OfficialZonesGeoJson is null)
        {
            return NotFound();
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(settings.OfficialZonesGeoJson);
        return File(bytes, "application/geo+json", $"official-zones-{settings.Year}.geojson");
    }

    [HttpPost("BarrioMap/Admin/DeleteOfficialZones")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOfficialZones(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }
        await _cityPlanningService.DeleteOfficialZonesAsync(user.Id, cancellationToken);
        SetSuccess("Official zones deleted.");
        return RedirectToAction(nameof(Admin));
    }

    // ======================================================================
    // Containers
    // ======================================================================

    [HttpGet("ContainerMap/{year:int}")]
    public async Task<IActionResult> ContainerMap(int year, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        var isMapAdmin = await IsMapAdminAsync(user.Id, cancellationToken);
        var userSeasonId = await _campService.GetCampLeadSeasonIdForYearAsync(user.Id, year, cancellationToken);
        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);

        if (!isMapAdmin && (!settings.IsContainerPlacementOpen || !userSeasonId.HasValue))
        {
            return Forbid();
        }

        string campSlug = string.Empty;
        string campName = string.Empty;
        if (!isMapAdmin && userSeasonId.HasValue)
        {
            var displayData = await _campService.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken);
            if (displayData.TryGetValue(userSeasonId.Value, out var data))
            {
                campSlug = data.CampSlug;
                campName = data.Name;
            }
        }

        return View(new ContainerMapViewModel
        {
            Year = year,
            IsMapAdmin = isMapAdmin,
            UserCampSeasonId = userSeasonId?.ToString() ?? string.Empty,
            CampSlug = campSlug,
            CampName = campName,
        });
    }

    [HttpGet("BarrioMap/Admin/Containers/{year}")]
    public async Task<IActionResult> Containers(int year, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        var allContainers = await _containerService.GetAllByYearAsync(year, cancellationToken);
        var seasonDisplayData = await _campService.GetCampSeasonDisplayDataForYearAsync(year, cancellationToken);

        var bySeasonId = allContainers
            .Where(c => c.CampSeasonId is not null)
            .GroupBy(c => c.CampSeasonId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var vm = new OrgContainerIndexViewModel
        {
            Year = year,
            IsContainerPlacementOpen = settings.IsContainerPlacementOpen,
            OrgContainers = allContainers
                .Where(c => c.CampSeasonId is null)
                .Select(ToContainerViewModel)
                .ToList(),
            BarrioGroups = seasonDisplayData
                .OrderBy(kvp => kvp.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new BarrioContainerGroup
                {
                    SeasonId = kvp.Key,
                    CampName = kvp.Value.Name,
                    CampSlug = kvp.Value.CampSlug,
                    Containers = bySeasonId.TryGetValue(kvp.Key, out var cs)
                        ? cs.Select(ToContainerViewModel).ToList()
                        : []
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
        IsPlaced = c.LocationGeoJson is not null,
        PlacementNotes = c.PlacementNotes,
        PlacementImageUrl = c.PlacementImageStoragePath,
        PlacementImageFileName = c.PlacementImageFileName,
    };

    [HttpPost("BarrioMap/Admin/Containers/{year}/Barrios/{seasonId}/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBarrioContainer(int year, Guid seasonId, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        var season = await _campService.GetCampSeasonByIdAsync(seasonId, cancellationToken);
        if (season is null || season.Year != year) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Containers), new { year });
        }

        try
        {
            await _containerService.CreateAsync(model.ToContainerData(seasonId, year), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Containers), new { year });
        }

        SetSuccess("Container added.");
        return RedirectToAction(nameof(Containers), new { year });
    }

    [HttpPost("BarrioMap/Admin/Containers/{year}/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOrgContainer(int year, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Containers), new { year });
        }

        try
        {
            await _containerService.CreateAsync(model.ToContainerData(null, year), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Containers), new { year });
        }

        SetSuccess("Container added.");
        return RedirectToAction(nameof(Containers), new { year });
    }

    [HttpPost("BarrioMap/Admin/Containers/{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditOrgContainer(Guid id, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(Containers), new { year = container.Year });
        }

        try
        {
            await _containerService.UpdateAsync(id, model.ToContainerData(container.CampSeasonId, container.Year), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
            return RedirectToAction(nameof(Containers), new { year = container.Year });
        }

        SetSuccess("Container updated.");
        return RedirectToAction(nameof(Containers), new { year = container.Year });
    }

    [HttpPost("BarrioMap/Admin/Containers/{id}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOrgContainer(Guid id, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
        {
            return Forbid();
        }

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var year = container.Year;
        await _containerService.DeleteAsync(id, cancellationToken);
        SetSuccess("Container deleted.");
        return RedirectToAction(nameof(Containers), new { year });
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
