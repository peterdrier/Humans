using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models.CampAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]
[Route("Barrios/Admin")]
[Route("Camps/Admin")]
public class CampAdminController(
    ICampService campService,
    ICampRoleService campRoleService,
    ICityPlanningService cityPlanningService,
    CampAdminPageBuilder campAdminPageBuilder,
    CampCsvExportBuilder campCsvExportBuilder,
    IUserService userService,
    ILogger<CampAdminController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        try
        {
            return View(await campAdminPageBuilder.BuildAsync());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Barrios admin page");
            SetError("Failed to load Barrios admin page.");
            return RedirectToAction(nameof(AdminController.Index), "Admin");
        }
    }

    [HttpPost("Approve/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid seasonId, string? notes)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return Unauthorized();

        try
        {
            await campService.ApproveSeasonAsync(seasonId, user.Id, notes);
            SetSuccess("Season approved.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to approve camp season {SeasonId} for admin {UserId}", seasonId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Reject/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid seasonId, string notes)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(notes))
        {
            SetError("Rejection notes are required.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await campService.RejectSeasonAsync(seasonId, user.Id, notes);
            SetSuccess("Season rejected.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to reject camp season {SeasonId} for admin {UserId}", seasonId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("OpenSeason")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenSeason([FromForm] int year)
    {
        try
        {
            await campService.OpenSeasonAsync(year);
            SetSuccess($"Season {year} opened for registration.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open season {Year}", year);
            SetError($"Failed to open season: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("CloseSeason/{year:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseSeason(int year)
    {
        try
        {
            await campService.CloseSeasonAsync(year);
            SetSuccess($"Season {year} closed for registration.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to close season {Year}", year);
            SetError($"Failed to close season: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetPublicYear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPublicYear(int year)
    {
        try
        {
            await campService.SetPublicYearAsync(year);
            SetSuccess($"Public year set to {year}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set public year to {Year}", year);
            SetError($"Failed to set public year: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetNameLockDate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNameLockDate(int year, string lockDate)
    {
        var parseResult = NodaTime.Text.LocalDatePattern.Iso.Parse(lockDate);
        if (!parseResult.Success)
        {
            SetError("Invalid date format.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await campService.SetNameLockDateAsync(year, parseResult.Value);
            SetSuccess($"Name lock date for {year} set to {parseResult.Value}.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set name lock date for {Year}", year);
            SetError($"Failed to set name lock date: {ex.Message}");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetCampSeasonEeSlotCount/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCampSeasonEeSlotCount(
        Guid seasonId, int slotCount, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return Unauthorized();

        try
        {
            await campService.SetCampSeasonEeSlotCountAsync(
                seasonId, slotCount, user.Id, cancellationToken);
            SetSuccess($"EE slot count set to {slotCount}.");
        }
        catch (ArgumentOutOfRangeException)
        {
            logger.LogWarning(
                "EE slot count must be non-negative for season {SeasonId} (actor {UserId})",
                seasonId, user.Id);
            SetError("EE slot count cannot be negative.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "Failed to set EE slot count on season {SeasonId}: {Reason}",
                seasonId, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetEeStartDate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEeStartDate(string? eeStartDate, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return Unauthorized();

        var (ok, parsed, parseError) = TryParseEeStartDate(eeStartDate);
        if (!ok)
        {
            SetError(parseError!);
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await campService.SetEeStartDateAsync(parsed, user.Id, cancellationToken);
            SetSuccess(EeStartDateSuccessMessage(parsed));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set EE start date");
            SetError($"Failed to set EE start date: {ex.Message}");
        }

        return RedirectToAction(nameof(Index));
    }

    private static string EeStartDateSuccessMessage(LocalDate? date) =>
        date.HasValue ? $"EE start date set to {date.Value.ToDisplayDate()}." : "EE start date cleared.";

    private static (bool Ok, LocalDate? Value, string? Error) TryParseEeStartDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return (true, null, null);
        var result = NodaTime.Text.LocalDatePattern.Iso.Parse(input);
        return result.Success
            ? (true, result.Value, null)
            : (false, null, "Invalid date format. Use yyyy-MM-dd.");
    }

    [HttpPost("Reactivate/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(Guid seasonId, string? returnSlug)
    {
        try
        {
            await campService.ReactivateSeasonAsync(seasonId);
            SetSuccess("Season status updated.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to reactivate camp season {SeasonId}", seasonId);
            SetError(ex.Message);
        }

        if (!string.IsNullOrEmpty(returnSlug))
            return RedirectToAction(nameof(CampController.Details), "Camp", new { slug = returnSlug });
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export")]
    public async Task<IActionResult> ExportCamps()
    {
        try
        {
            var export = await campCsvExportBuilder.BuildAsync();
            return File(export.Content, export.ContentType, export.FileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export barrios");
            SetError("Failed to export barrios.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("UpdateRegistrationInfo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRegistrationInfo([FromForm] string? registrationInfo)
    {
        try
        {
            await cityPlanningService.UpdateRegistrationInfoAsync(registrationInfo);
            SetSuccess("Registration info updated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update registration info");
            SetError("Failed to update registration info.");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Delete([FromForm] Guid campId)
    {
        try
        {
            await campService.DeleteCampAsync(campId);
            SetSuccess("Camp deleted.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to delete camp {CampId}", campId);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Roles")]
    public async Task<IActionResult> Roles(CancellationToken ct)
    {
        var defs = await campRoleService.ListDefinitionsAsync(includeDeactivated: true, ct);
        var settings = await campService.GetSettingsAsync(ct);
        var year = settings.PublicYear;
        CampRoleDefinitionListRowViewModel MapRow(CampRoleDefinitionInfo d) =>
            new(d.Id, d.Name, d.Slug, d.Description, d.SlotCount, d.MinimumRequired, d.SortOrder, d.IsActive,
                string.IsNullOrWhiteSpace(d.Slug) ? null : campRoleService.BuildGroupKey(year, d.Slug));
        var active = defs.Where(d => d.IsActive).Select(MapRow).ToList();
        var deactivated = defs.Where(d => !d.IsActive).Select(MapRow).ToList();
        return View(new CampRoleDefinitionListViewModel
        {
            Active = active,
            Deactivated = deactivated,
            PublicYear = year,
        });
    }

    [HttpGet("Roles/{slug}")]
    public async Task<IActionResult> RolesDrillDown(string slug, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug)) return NotFound();

        // Slug → GUID fallback per memory/architecture/slug-routes-fallback-to-guid.md.
        // Slugs are user-controlled and may be empty ("" = no group yet); the GUID
        // form is always reachable.
        var info = await campRoleService.GetDefinitionBySlugAsync(slug, ct);
        if (info is null && Guid.TryParse(slug, out var roleId))
            info = await campRoleService.GetDefinitionByIdAsync(roleId, ct);
        if (info is null) return NotFound();

        var settings = await campService.GetSettingsAsync(ct);
        var resolvedYear = year ?? settings.PublicYear;

        var data = await campRoleService.BuildDrillDownAsync(info.Id, resolvedYear, ct);
        if (data is null) return NotFound();

        // Year picker options: union of open seasons, public year, and the resolved year
        // (so a manually-entered year remains selectable). Sorted descending — most recent first.
        var yearOptions = new HashSet<int>(settings.OpenSeasons) { settings.PublicYear, resolvedYear };

        var vm = new CampRoleDrillDownViewModel
        {
            Slug = data.Definition.Slug,
            RouteKey = string.IsNullOrWhiteSpace(data.Definition.Slug)
                ? data.Definition.Id.ToString()
                : data.Definition.Slug,
            RoleName = data.Definition.Name,
            Description = data.Definition.Description,
            SlotCount = data.Definition.SlotCount,
            MinimumRequired = data.Definition.MinimumRequired,
            Year = data.Year,
            GroupEmail = data.GroupEmail,
            YearOptions = yearOptions.OrderByDescending(y => y).ToList(),
            Camps = data.Rows
                .OrderBy(r => r.CampName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new CampRoleDrillDownCampRowViewModel(
                    r.CampId, r.CampName, r.CampSlug, r.CampSeasonId,
                    r.Assignees
                        .OrderBy(a => a.AssignedAt)
                        .Select(a => new CampRoleDrillDownAssigneeViewModel(a.UserId))
                        .ToList()))
                .ToList(),
        };
        return View("RoleDrillDown", vm);
    }

    private IActionResult RedirectToRolesWithSuccess(string message)
    {
        SetSuccess(message);
        return RedirectToAction(nameof(Roles));
    }

    [HttpGet("Roles/Create")]
    public IActionResult CreateRole() => View("RoleForm", new CampRoleDefinitionFormViewModel());

    [HttpPost("Roles/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRole(CampRoleDefinitionFormViewModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return View("RoleForm", form);

        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return Unauthorized();

        try
        {
            var input = new CreateCampRoleDefinitionInput(
                form.Name, form.Slug, form.Description, form.SlotCount, form.MinimumRequired, form.SortOrder);
            await campRoleService.CreateDefinitionAsync(input, user.Id, ct);
            SetSuccess($"Created camp role '{form.Name}'.");
            return RedirectToAction(nameof(Roles));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("RoleForm", form);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CreateRole failed.");
            ModelState.AddModelError(string.Empty, "Failed to create role definition.");
            return View("RoleForm", form);
        }
    }

    [HttpGet("Roles/{id:guid}/Edit")]
    public async Task<IActionResult> EditRole(Guid id, CancellationToken ct)
    {
        var def = await campRoleService.GetDefinitionByIdAsync(id, ct);
        if (def is null) return NotFound();
        return View("RoleForm", new CampRoleDefinitionFormViewModel
        {
            Id = def.Id,
            Name = def.Name,
            Slug = def.Slug,
            Description = def.Description,
            SlotCount = def.SlotCount,
            MinimumRequired = def.MinimumRequired,
            SortOrder = def.SortOrder,
        });
    }

    [HttpPost("Roles/{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRole(Guid id, CampRoleDefinitionFormViewModel form, CancellationToken ct)
    {
        form.Id = id;
        if (!ModelState.IsValid)
            return View("RoleForm", form);

        var user = await GetCurrentUserInfoAsync();
        if (user is null) return Unauthorized();

        try
        {
            var input = new UpdateCampRoleDefinitionInput(
                form.Name, form.Slug, form.Description, form.SlotCount, form.MinimumRequired, form.SortOrder);
            var result = await campRoleService.UpdateDefinitionAsync(id, input, user.Id, ct);
            return result.Status switch
            {
                UpdateCampRoleDefinitionStatus.Updated => RedirectToRolesWithSuccess(result.SuccessMessage),
                UpdateCampRoleDefinitionStatus.NotFound => NotFound(),
                _ => throw new InvalidOperationException($"Unexpected camp role update status '{result.Status}'.")
            };
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("RoleForm", form);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EditRole failed for {RoleId}.", id);
            ModelState.AddModelError(string.Empty, "Failed to update role definition.");
            return View("RoleForm", form);
        }
    }

    [HttpPost("Roles/{id:guid}/Deactivate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeactivateRole(Guid id, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return Unauthorized();

        try
        {
            var ok = await campRoleService.DeactivateDefinitionAsync(id, user.Id, ct);
            if (!ok) return NotFound();
            SetSuccess("Camp role deactivated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeactivateRole failed for {RoleId}.", id);
            SetError("Failed to deactivate camp role.");
        }
        return RedirectToAction(nameof(Roles));
    }

    [HttpPost("Roles/{id:guid}/Reactivate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReactivateRole(Guid id, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return Unauthorized();

        try
        {
            var ok = await campRoleService.ReactivateDefinitionAsync(id, user.Id, ct);
            if (!ok) return NotFound();
            SetSuccess("Camp role reactivated.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReactivateRole failed for {RoleId}.", id);
            SetError("Failed to reactivate camp role.");
        }
        return RedirectToAction(nameof(Roles));
    }

    [HttpGet("Compliance")]
    public async Task<IActionResult> Compliance(int? year, CancellationToken ct)
    {
        var settings = await campService.GetSettingsAsync(ct);
        var resolvedYear = year ?? settings.PublicYear;
        var report = await campRoleService.GetComplianceReportAsync(resolvedYear, ct);

        var vm = new CampRoleComplianceViewModel
        {
            Year = report.Year,
            Camps = report.Camps.Select(c => new CampRoleComplianceCampRowViewModel(
                c.CampId, c.CampName, c.CampSlug, c.CampSeasonId,
                c.Roles.Select(r => new CampRoleComplianceRoleRowViewModel(r.DefinitionName, r.MinimumRequired, r.Filled, r.IsMet)).ToList(),
                c.IsCompliant)).ToList(),
        };
        return View(vm);
    }
}
