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
    IShiftObligationService shiftObligationService,
    CampAdminPageBuilder campAdminPageBuilder,
    CampCsvExportBuilder campCsvExportBuilder,
    IUserServiceRead userService,
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

    [HttpPost("SeedSystemRoles")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SeedSystemRoles(CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return Unauthorized();

        try
        {
            var result = await campRoleService.SeedSystemRolesAndMigrateLeadsAsync(user.Id, ct);
            var summary =
                $"System roles: {result.DefinitionsCreated} created. " +
                $"Camp leads: {result.LeadsMigrated} migrated, " +
                $"{result.LeadsAlreadyMigrated} already migrated, " +
                $"{result.LeadsSkipped} skipped" +
                (result.SkippedCampSlugs.Count == 0
                    ? "."
                    : $" ({string.Join(", ", result.SkippedCampSlugs)}).");
            SetSuccess(summary);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SeedSystemRoles failed.");
            SetError("Failed to seed system roles and migrate leads.");
        }
        return RedirectToAction(nameof(Index));
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

    // --- Barrio shift obligations (see docs/sections + the design spec) ---

    [HttpGet("ShiftObligations")]
    public async Task<IActionResult> ShiftObligations(int? year, CancellationToken ct)
    {
        var settings = await campService.GetSettingsAsync(ct);
        var resolvedYear = year ?? settings.PublicYear;
        var matrix = await shiftObligationService.GetComplianceMatrixAsync(resolvedYear, ct);

        var vm = new ShiftObligationMatrixViewModel
        {
            Year = matrix.Year,
            Columns = matrix.Columns
                .Select(c => new ShiftObligationColumnViewModel(
                    c.ShiftObligationId, c.Name, c.TargetUrl, c.Applicability))
                .ToList(),
            Rows = matrix.Rows
                .OrderBy(r => r.BarrioName, StringComparer.OrdinalIgnoreCase)
                .Select(r => new ShiftObligationBarrioRowViewModel(
                    r.CampSeasonId, r.BarrioName, r.Slug, r.ActiveMemberCount,
                    r.Cells.Select(cell => new ShiftObligationCellViewModel(
                        cell.ShiftObligationId, cell.Applicable, cell.Done, cell.Required, cell.UnderMembered))
                        .ToList()))
                .ToList(),
            ExemptNobodiesOrg = matrix.ExemptNobodiesOrg
                .OrderBy(e => e.BarrioName, StringComparer.OrdinalIgnoreCase)
                .Select(e => new ShiftObligationExemptViewModel(e.CampSeasonId, e.BarrioName, e.ActiveMemberCount))
                .ToList(),
            OffGridForPower = matrix.OffGridForPower
                .OrderBy(o => o.BarrioName, StringComparer.OrdinalIgnoreCase)
                .Select(o => new ShiftObligationOffGridViewModel(o.CampSeasonId, o.BarrioName, o.Reason))
                .ToList(),
        };
        return View(vm);
    }

    [HttpGet("ShiftObligations/{campSeasonId:guid}")]
    public async Task<IActionResult> ShiftObligationDetail(Guid campSeasonId, CancellationToken ct)
    {
        var detail = await shiftObligationService.GetBarrioObligationDetailAsync(campSeasonId, ct);
        if (detail is null) return NotFound();
        return View("ShiftObligationDetail", MapDetail(detail, showActions: true));
    }

    private static ShiftObligationDetailViewModel MapDetail(BarrioObligationDetail detail, bool showActions) =>
        new()
        {
            CampSeasonId = detail.CampSeasonId,
            BarrioName = detail.BarrioName,
            ShowActions = showActions,
            Functions = detail.Functions
                .Select(f => new ShiftObligationDetailFunctionViewModel(
                    f.ShiftObligationId, f.Name, f.Done, f.Required,
                    f.SignedUp.Select(s => new ShiftObligationSignedUpMemberViewModel(s.UserId, s.Name, s.Count)).ToList(),
                    f.NotYetSignedUpNames))
                .ToList(),
        };

    [HttpPost("ShiftObligations/Remind")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemindShiftObligation(
        Guid campSeasonId, Guid shiftObligationId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return Unauthorized();

        try
        {
            await shiftObligationService.SendReminderAsync(campSeasonId, shiftObligationId, user.Id, ct);
            SetSuccess("Reminder sent.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send shift-obligation reminder for season {SeasonId}, function {FunctionId}",
                campSeasonId, shiftObligationId);
            SetError("Failed to send reminder.");
        }

        return RedirectToAction(nameof(ShiftObligations));
    }

    [HttpPost("ShiftObligations/RemindAllNonCompliant")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemindAllNonCompliant(Guid shiftObligationId, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return Unauthorized();

        try
        {
            var count = await shiftObligationService.RemindAllNonCompliantAsync(shiftObligationId, user.Id, ct);
            SetSuccess($"{count} barrio(s) reminded.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remind all non-compliant barrios for function {FunctionId}", shiftObligationId);
            SetError("Failed to send reminders.");
        }

        return RedirectToAction(nameof(ShiftObligations));
    }

    [HttpPost("ShiftObligations/SetOverride")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetShiftObligationOverride(
        Guid campSeasonId, Guid shiftObligationId, int? requiredShiftCount, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return Unauthorized();

        try
        {
            await shiftObligationService.SetOverrideAsync(
                campSeasonId, shiftObligationId, requiredShiftCount, user.Id, ct);
            SetSuccess(requiredShiftCount.HasValue ? "Required-shift override set." : "Override cleared.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set override for season {SeasonId}, function {FunctionId}",
                campSeasonId, shiftObligationId);
            SetError("Failed to set override.");
        }

        return RedirectToAction(nameof(ShiftObligations));
    }

    [HttpGet("ShiftObligations/Functions")]
    public async Task<IActionResult> ShiftObligationFunctions(Guid? editId, CancellationToken ct)
    {
        var functions = await shiftObligationService.GetFunctionsAsync(ct);
        var roleDefs = await campRoleService.ListDefinitionsAsync(includeDeactivated: false, ct);

        // Pre-fill the form from the row the admin asked to edit (reusing the list
        // fetch above — no GetById on the service). Unknown editId falls through to
        // a blank create form with a warning.
        ShiftObligationConfigInfo? editTarget = null;
        if (editId is { } id)
        {
            editTarget = functions.FirstOrDefault(f => f.Id == id);
            if (editTarget is null)
                SetInfo("That function no longer exists — showing a blank create form.");
        }

        return View("ShiftObligationFunctions", BuildFunctionsViewModel(functions, roleDefs, editTarget));
    }

    [HttpPost("ShiftObligations/Functions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShiftObligationFunctions(
        ShiftObligationFunctionFormViewModel form, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        if (user is null) return Unauthorized();

        var input = new ShiftObligationConfigInput(
            form.Id, form.TargetType, form.TargetId, form.CampRoleSlug,
            form.Applicability, form.DefaultRequiredShiftCount, form.IsActive, form.SortOrder);

        UpsertFunctionResult result;
        try
        {
            result = await shiftObligationService.UpsertFunctionAsync(input, user.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upsert shift-obligation function {FunctionId}", form.Id);
            SetError("Failed to save function.");
            return RedirectToAction(nameof(ShiftObligationFunctions));
        }

        switch (result)
        {
            case UpsertFunctionResult.Saved:
                SetSuccess("Function saved.");
                break;
            case UpsertFunctionResult.DuplicateTarget:
                SetError("A function already targets that team or rota. Each target may be used once.");
                break;
            case UpsertFunctionResult.NotFound:
                SetError("That function no longer exists.");
                break;
        }

        return RedirectToAction(nameof(ShiftObligationFunctions));
    }

    private static ShiftObligationFunctionsViewModel BuildFunctionsViewModel(
        IReadOnlyList<ShiftObligationConfigInfo> functions,
        IReadOnlyList<CampRoleDefinitionInfo> roleDefs,
        ShiftObligationConfigInfo? editTarget = null)
    {
        var slugOptions = roleDefs
            .Where(d => !string.IsNullOrWhiteSpace(d.Slug))
            .GroupBy(d => d.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new CampRoleSlugOptionViewModel(d.Slug, d.Name))
            .ToList();

        // Preserve a stored slug that isn't in the role-definition catalogue (e.g. a
        // legacy free-text "power") by prepending it as an explicit option, so editing
        // doesn't silently reset it to "none".
        if (editTarget is not null
            && !string.IsNullOrWhiteSpace(editTarget.CampRoleSlug)
            && !slugOptions.Any(o => string.Equals(o.Slug, editTarget.CampRoleSlug, StringComparison.OrdinalIgnoreCase)))
        {
            slugOptions.Insert(0, new CampRoleSlugOptionViewModel(
                editTarget.CampRoleSlug, "(not a defined role)"));
        }

        return new ShiftObligationFunctionsViewModel
        {
            Functions = functions
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.TargetName, StringComparer.OrdinalIgnoreCase)
                .Select(f => new ShiftObligationFunctionRowViewModel(
                    f.Id, f.TargetType, f.TargetId, f.TargetName, f.CampRoleSlug,
                    f.Applicability, f.DefaultRequiredShiftCount, f.IsActive, f.SortOrder))
                .ToList(),
            CampRoleSlugOptions = slugOptions,
            EditTargetName = editTarget is null
                ? null
                : (string.IsNullOrWhiteSpace(editTarget.TargetName)
                    ? editTarget.TargetId.ToString()
                    : editTarget.TargetName),
            Form = editTarget is null
                ? new ShiftObligationFunctionFormViewModel()
                : new ShiftObligationFunctionFormViewModel
                {
                    Id = editTarget.Id,
                    TargetType = editTarget.TargetType,
                    TargetId = editTarget.TargetId,
                    CampRoleSlug = editTarget.CampRoleSlug,
                    Applicability = editTarget.Applicability,
                    DefaultRequiredShiftCount = editTarget.DefaultRequiredShiftCount,
                    IsActive = editTarget.IsActive,
                    SortOrder = editTarget.SortOrder,
                },
        };
    }
}
