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

    [HttpPost("Admin/OpenContainerPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenContainerPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        await _cityPlanningService.OpenContainerPlacementAsync(user.Id, cancellationToken);
        SetSuccess("Container placement phase opened.");
        return RedirectToAction(nameof(OrgContainers), new { year = settings.Year });
    }

    [HttpPost("Admin/CloseContainerPlacement")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseContainerPlacement(CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var settings = await _cityPlanningService.GetSettingsAsync(cancellationToken);
        await _cityPlanningService.CloseContainerPlacementAsync(user.Id, cancellationToken);
        SetSuccess("Container placement phase closed.");
        return RedirectToAction(nameof(OrgContainers), new { year = settings.Year });
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

    // ======================================================================
    // Org-level containers
    // ======================================================================

    [HttpGet("Admin/Containers/{year}")]
    public async Task<IActionResult> OrgContainers(int year, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var allContainers = await _containerService.GetAllByYearAsync(year, cancellationToken);
        var seasonBriefs = await _campService.GetCampSeasonBriefsForYearAsync(year, cancellationToken);

        var bySeasonId = allContainers
            .Where(c => c.CampSeasonId is not null)
            .GroupBy(c => c.CampSeasonId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var vm = new OrgContainerIndexViewModel
        {
            Year = year,
            OrgContainers = allContainers
                .Where(c => c.CampSeasonId is null)
                .Select(ToContainerViewModel)
                .ToList(),
            BarrioGroups = seasonBriefs
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => new BarrioContainerGroup
                {
                    SeasonId = s.CampSeasonId,
                    CampName = s.Name,
                    CampSlug = s.CampSlug,
                    Containers = bySeasonId.TryGetValue(s.CampSeasonId, out var cs)
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
        ImageFileName = c.ImageFileName
    };

    [HttpPost("Admin/Containers/{year}/Barrios/{seasonId}/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBarrioContainer(int year, Guid seasonId, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(OrgContainers), new { year });
        }

        await _containerService.CreateAsync(new ContainerData(
            CampSeasonId: seasonId,
            Year: year,
            Name: model.Name,
            Description: model.Description), cancellationToken);

        SetSuccess("Container added.");
        return RedirectToAction(nameof(OrgContainers), new { year });
    }

    [HttpPost("Admin/Containers/{year}/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOrgContainer(int year, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(OrgContainers), new { year });
        }

        await _containerService.CreateAsync(new ContainerData(
            CampSeasonId: null,
            Year: year,
            Name: model.Name,
            Description: model.Description), cancellationToken);

        SetSuccess("Container added.");
        return RedirectToAction(nameof(OrgContainers), new { year });
    }

    [HttpPost("Admin/Containers/{id}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditOrgContainer(Guid id, ContainerFormModel model, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Please correct the validation errors.");
            return RedirectToAction(nameof(OrgContainers), new { year = container.Year });
        }

        await _containerService.UpdateAsync(id, new ContainerData(
            CampSeasonId: null,
            Year: container.Year,
            Name: model.Name,
            Description: model.Description), cancellationToken);

        SetSuccess("Container updated.");
        return RedirectToAction(nameof(OrgContainers), new { year = container.Year });
    }

    [HttpPost("Admin/Containers/{id}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOrgContainer(Guid id, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var year = container.Year;
        await _containerService.DeleteAsync(id, cancellationToken);
        SetSuccess("Container deleted.");
        return RedirectToAction(nameof(OrgContainers), new { year });
    }

    [HttpPost("Admin/Containers/{id}/Image/Upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadOrgContainerImage(Guid id, IFormFile? file, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        if (file is null || file.Length == 0)
        {
            SetError("Please select a file to upload.");
            return RedirectToAction(nameof(OrgContainers), new { year = container.Year });
        }

        try
        {
            await _containerService.UploadImageAsync(id, file.OpenReadStream(), file.FileName, file.ContentType, file.Length, cancellationToken);
            SetSuccess("Image uploaded.");
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(OrgContainers), new { year = container.Year });
    }

    [HttpPost("Admin/Containers/{id}/Image/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOrgContainerImage(Guid id, CancellationToken cancellationToken)
    {
        var (error, user) = await RequireCurrentUserAsync();
        if (error != null) return error;

        if (!await IsMapAdminAsync(user.Id, cancellationToken))
            return Forbid();

        var container = await _containerService.GetByIdAsync(id, cancellationToken);
        if (container is null) return NotFound();

        var year = container.Year;
        await _containerService.DeleteImageAsync(id, cancellationToken);
        SetSuccess("Image removed.");
        return RedirectToAction(nameof(OrgContainers), new { year });
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
