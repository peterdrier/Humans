using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Camps.Controllers;

[Authorize(Policy = PolicyNames.CampAdminOrAdmin)]
[Route("Barrios/Admin")]
[Route("Camps/Admin")]
internal sealed class CampAdminController(
    ICampManagement campService,
    ICampRoleService campRoleService,
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
            return View(await campAdminPageBuilder.BuildAsync(HttpContext.RequestAborted));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Barrios admin page");
            SetError("Failed to load Barrios admin page.");
            return RedirectToAction("Index", "Admin");
        }
    }

    [HttpPost("Approve/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid seasonId, string? notes)
    {
        var user = await GetCurrentUserInfoAsync();
        if (user is null) return Unauthorized();

        var result = await campService.ApproveSeasonAsync(seasonId, user.Id, notes);
        if (result.Succeeded)
        {
            SetSuccess("Season approved.");
        }
        else
        {
            logger.LogWarning("Failed to approve camp season {SeasonId} for admin {UserId}: {Reason}", seasonId, user.Id, result.ErrorMessage);
            SetError(result.ErrorMessage!);
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

        var result = await campService.RejectSeasonAsync(seasonId, user.Id, notes);
        if (result.Succeeded)
        {
            SetSuccess("Season rejected.");
        }
        else
        {
            logger.LogWarning("Failed to reject camp season {SeasonId} for admin {UserId}: {Reason}", seasonId, user.Id, result.ErrorMessage);
            SetError(result.ErrorMessage!);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("OpenSeason")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenSeason([FromForm] int year)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        try
        {
            await campService.OpenSeasonAsync(year, user.Id);
            SetSuccess($"Season {year} opened for registration.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to open season {Year}", year);
            SetError($"Failed to open season: {ex.Message}");
        }
        // Posted from the /Settings#barrios tab, so post-redirect-get lands back there.
        return Redirect("/Settings#barrios");
    }

    [HttpPost("CloseSeason/{year:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseSeason(int year)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        try
        {
            await campService.CloseSeasonAsync(year, user.Id);
            SetSuccess($"Season {year} closed for registration.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to close season {Year}", year);
            SetError($"Failed to close season: {ex.Message}");
        }
        // Posted from the /Settings#barrios tab, so post-redirect-get lands back there.
        return Redirect("/Settings#barrios");
    }

    [HttpPost("SetNameLockDate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetNameLockDate(int year, string lockDate)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var parseResult = NodaTime.Text.LocalDatePattern.Iso.Parse(lockDate);
        if (!parseResult.Success)
        {
            SetError("Invalid date format.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await campService.SetNameLockDateAsync(year, parseResult.Value, user.Id);
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
        if (!ModelState.IsValid) return BadRequest(ModelState);

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

    [HttpPost("Reactivate/{seasonId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(Guid seasonId, string? returnSlug)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var result = await campService.ReactivateSeasonAsync(seasonId, actorUserId: user.Id);
        if (result.Succeeded)
        {
            SetSuccess("Season status updated.");
        }
        else
        {
            logger.LogWarning("Failed to reactivate camp season {SeasonId}: {Reason}", seasonId, result.ErrorMessage);
            SetError(result.ErrorMessage!);
        }

        if (!string.IsNullOrEmpty(returnSlug))
            return RedirectToAction(nameof(CampController.Details), "Camp", new { slug = returnSlug });
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Export")]
    public async Task<IActionResult> ExportCamps()
    {
        var actorUserId = GetCurrentUserId();
        if (actorUserId is null) return Unauthorized();

        try
        {
            var export = await campCsvExportBuilder.BuildAsync(actorUserId.Value, HttpContext.RequestAborted);
            return File(export.Content, export.ContentType, export.FileName);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to export barrios");
            SetError("Failed to export barrios.");
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpPost("Delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Delete([FromForm] Guid campId)
    {
        var user = await GetCurrentUserInfoAsync(HttpContext.RequestAborted);
        if (user is null) return Unauthorized();

        var result = await campService.DeleteCampAsync(campId, actorUserId: user.Id);
        if (result.Succeeded)
        {
            SetSuccess("Camp deleted.");
        }
        else
        {
            logger.LogWarning("Failed to delete camp {CampId}: {Reason}", campId, result.ErrorMessage);
            SetError(result.ErrorMessage!);
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

        // Slugs are user-controlled and may be empty ("" = no group yet), so the GUID form stays reachable.
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
            var result = await campRoleService.CreateDefinitionAsync(input, user.Id, ct);
            if (!result.Succeeded)
            {
                logger.LogWarning("CreateRole rejected for actor {UserId}: {Reason}", user.Id, result.ErrorMessage);
                ModelState.AddModelError(string.Empty, result.ErrorMessage!);
                return View("RoleForm", form);
            }
            SetSuccess($"Created camp role '{form.Name}'.");
            return RedirectToAction(nameof(Roles));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            if (result.Status == UpdateCampRoleDefinitionStatus.Updated)
            {
                SetSuccess(result.SuccessMessage);
                return RedirectToAction(nameof(Roles));
            }

            if (result.Status == UpdateCampRoleDefinitionStatus.NotFound)
            {
                return NotFound();
            }

            if (result.Status == UpdateCampRoleDefinitionStatus.Rejected)
            {
                logger.LogWarning("EditRole rejected for role {RoleId} by actor {UserId}: {Reason}", id, user.Id, result.ErrorMessage);
                ModelState.AddModelError(string.Empty, result.ErrorMessage!);
                return View("RoleForm", form);
            }

            throw new System.Diagnostics.UnreachableException($"Unexpected camp role update status '{result.Status}'.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            var definitionsCreated = await campRoleService.SeedSystemRolesAsync(user.Id, ct);
            SetSuccess($"System roles: {definitionsCreated} created.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SeedSystemRoles failed.");
            SetError("Failed to seed system roles.");
        }
        return RedirectToAction(nameof(Index));
    }
}
