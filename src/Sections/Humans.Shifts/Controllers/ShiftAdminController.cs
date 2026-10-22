using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;
using Humans.Base.Authorization;
using Humans.Users.Contracts;

namespace Humans.Shifts.Controllers;

[Authorize]
[Route("Teams/{slug}/Shifts")]
internal sealed class ShiftAdminController(
    ITeamService teamService,
    IShiftManagement shiftMgmt,
    IBurnSettingsService burnSettings,
    IShiftSignupService signupService,
    IShiftRowView shiftView,
    IUserService userService,
    IAuthorizationService authorizationService,
    IClock clock,
    ShiftAdminPageBuilder pageBuilder,
    ShiftVolunteerSearchBuilder volunteerSearchBuilder,
    IRotaCoordinatorMessageService rotaMessenger,
    ILogger<ShiftAdminController> logger,
    IStringLocalizer<ShiftsResource> localizer) : HumansTeamControllerBase(userService, teamService, authorizationService)
{
    private readonly ITeamService _teamService = teamService;

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, bool incompleteOnboarding = false, CancellationToken ct = default)
    {
        var (teamError, user, team) = await ResolveDepartmentAccessAsync(
            slug,
            async (resolvedTeam, resolvedUser) =>
                await CanManageDepartmentAsync(resolvedUser, resolvedTeam, ct)
                || await CanApproveDepartmentAsync(resolvedUser, resolvedTeam, ct), ct);
        if (teamError is not null) return teamError;

        var canManage = await CanManageDepartmentAsync(user, team, ct);
        var canApprove = await CanApproveDepartmentAsync(user, team, ct);
        var es = await burnSettings.GetActiveAsync(ct);
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction("Details", "Team", new { slug });
        }

        var teamEntity = await _teamService.GetTeamAsync(team.Id, ct);
        if (teamEntity is null) return NotFound();

        var model = await pageBuilder.BuildAsync(new ShiftAdminPageRequest(
            teamEntity,
            es,
            canManage,
            canApprove,
            ShiftRoleChecks.CanViewMedical(User),
            incompleteOnboarding,
            clock.GetCurrentInstant()), ct);

        return View(model);
    }

    private async Task<IActionResult> RenderInvalidEditAsync(string slug)
    {
        var result = await Index(slug);
        if (result is ViewResult view)
            view.ViewName = nameof(Index);
        return result;
    }

    [HttpPost("Rotas")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRota(string slug, CreateRotaModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
        if (es is null) return BadRequest("No active event.");

        if (!ModelState.IsValid)
        {
            SetError("Please fix the errors below.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = model.Name,
            Description = model.Description,
            Priority = model.Priority,
            Policy = model.Policy,
            Period = model.Period,
            PracticalInfo = model.PracticalInfo,
            CreatedAt = clock.GetCurrentInstant()
        };

        await shiftMgmt.CreateRotaAsync(rota, ParseTagIds(model.TagIds));

        SetSuccess($"Rota '{model.Name}' created.");
        return Redirect(Url.Action(nameof(Index), new { slug }) + "#rota-" + rota.Id.ToString("N"));
    }

    [HttpPost("Rotas/{rotaId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRota(string slug, Guid rotaId, EditRotaModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id);
        if (rota is null) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Please fix the errors below.");
            model.RotaId = rotaId;
            ViewData["InvalidRotaEdit"] = model;
            return await RenderInvalidEditAsync(slug);
        }

        if (!await shiftMgmt.UpdateRotaAsync(new UpdateRotaInput(
                rotaId,
                model.Name,
                model.Description,
                model.PracticalInfo,
                model.Priority,
                model.Policy,
                model.Period,
                rota.IsVisibleToVolunteers,
                ParseTagIds(model.TagIds))))
            return NotFound();

        SetSuccess($"Rota '{model.Name}' updated.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/ConfigureStaffing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureStaffing(string slug, Guid rotaId, StaffingGridModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            var result = await shiftMgmt.CreateBuildStrikeShiftsAsync(new ConfigureBuildStrikeStaffingInput(
                rotaId,
                team.Id,
                model.Days
                    .Select(d => new DayStaffingInput(d.DayOffset, d.MinVolunteers, d.MaxVolunteers))
                    .ToList()));
            if (result.Succeeded)
                SetSuccess(result.Message);
            else
                SetError(result.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Failed to configure staffing for rota {RotaId} in team {TeamId}: {Reason}", rotaId, team.Id, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/GenerateShifts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateShifts(string slug, Guid rotaId, GenerateEventShiftsModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var timeSlots = new List<ShiftTimeSlotInput>();
        foreach (var slot in model.TimeSlots)
        {
            if (!slot.StartTime.TryParseInvariantLocalTime(out var parsed))
            {
                SetError($"Invalid start time: {slot.StartTime}");
                return RedirectToAction(nameof(Index), new { slug });
            }

            timeSlots.Add(new ShiftTimeSlotInput(parsed, slot.DurationHours));
        }

        try
        {
            var result = await shiftMgmt.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
                rotaId,
                team.Id,
                model.StartDayOffset,
                model.EndDayOffset,
                timeSlots,
                model.MinVolunteers,
                model.MaxVolunteers));
            if (result.Succeeded)
                SetSuccess(result.Message);
            else
                SetError(result.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to generate shifts for rota {RotaId} in team {TeamId}", rotaId, team.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Shifts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateShift(string slug, CreateShiftModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        if (!ModelState.IsValid)
        {
            SetError("Please fix the errors below.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        if (!model.StartTime.TryParseInvariantLocalTime(out var parsedTime))
        {
            SetError("Invalid start time format.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        try
        {
            var result = await shiftMgmt.CreateShiftAsync(new CreateShiftInput(
                model.RotaId,
                team.Id,
                model.Description,
                model.DayOffset,
                parsedTime,
                model.DurationHours,
                model.MinVolunteers,
                model.MaxVolunteers,
                model.AdminOnly,
                IsAllDay: false));
            if (result.Succeeded)
                SetSuccess(result.Message);
            else
                SetError(result.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to create shift for rota {RotaId} in team {TeamId}", model.RotaId, team.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Shifts/{shiftId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditShift(string slug, Guid shiftId, EditShiftModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var shift = await GetShiftForTeamAsync(shiftId, team.Id);
        if (shift is null) return NotFound();

        if (!model.StartTime.TryParseInvariantLocalTime(out var parsedTime))
            ModelState.AddModelError(nameof(model.StartTime), "Invalid start time format.");

        if (!ModelState.IsValid)
        {
            SetError("Please fix the errors below.");
            model.ShiftId = shiftId;
            ViewData["InvalidShiftEdit"] = model;
            return await RenderInvalidEditAsync(slug);
        }

        var result = await shiftMgmt.UpdateShiftAsync(new UpdateShiftInput(
            shiftId,
            team.Id,
            model.Description,
            model.DayOffset,
            parsedTime,
            model.DurationHours,
            model.MinVolunteers,
            model.MaxVolunteers,
            model.AdminOnly));
        if (result.Succeeded)
            SetSuccess(result.Message);
        else
            SetError(result.Message);

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/ToggleVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleVisibility(string slug, Guid rotaId)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id);
        if (rota is null) return NotFound();

        var isVisible = !rota.IsVisibleToVolunteers;
        if (!await shiftMgmt.UpdateRotaAsync(new UpdateRotaInput(
                rota.Id,
                rota.Name,
                rota.Description,
                rota.PracticalInfo,
                rota.Priority,
                rota.Policy,
                rota.Period,
                isVisible,
                TagIds: null)))
            return NotFound();

        var label = isVisible ? "visible to" : "hidden from";
        SetSuccess($"Rota '{rota.Name}' is now {label} volunteers.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/Move")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.VolunteerManager)]
    public async Task<IActionResult> MoveRota(string slug, Guid rotaId, MoveRotaModel model)
    {
        var (teamError, user, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        try
        {
            var result = await shiftMgmt.MoveRotaToTeamAsync(new MoveRotaInput(
                rotaId,
                team.Id,
                model.TargetTeamId,
                user.Id));
            if (result.Succeeded)
            {
                SetSuccess(result.Message);
                return RedirectToAction(nameof(Index), new { slug = result.RedirectSlug });
            }

            SetError(result.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to move rota {RotaId} to team {TargetTeamId}", rotaId, model.TargetTeamId);
            SetError(ex.Message);
            return RedirectToAction(nameof(Index), new { slug });
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    // Management scope, so NoInfoAdmin is excluded.
    [HttpGet("Rotas/{rotaId}/Email")]
    public async Task<IActionResult> EmailRota(string slug, Guid rotaId, CancellationToken ct = default)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug, ct);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id, ct);
        if (rota is null) return NotFound();

        var recipientNames = await GetRotaRecipientNamesAsync(rota.Id, ct);

        var vm = new EmailRotaViewModel
        {
            RotaId = rota.Id,
            RotaName = rota.Name,
            TeamSlug = slug,
            RecipientCount = recipientNames.Count,
            RecipientNames = recipientNames
        };
        return View(vm);
    }

    [HttpPost("Rotas/{rotaId}/Email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailRota(string slug, Guid rotaId, EmailRotaViewModel model)
    {
        var (teamError, user, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id);
        if (rota is null) return NotFound();

        // Before any return-with-error path.
        var recipientNames = await GetRotaRecipientNamesAsync(rota.Id);
        model.RotaId = rota.Id;
        model.RotaName = rota.Name;
        model.TeamSlug = slug;
        model.RecipientCount = recipientNames.Count;
        model.RecipientNames = recipientNames;

        if (!ModelState.IsValid)
            return View(model);

        var result = await rotaMessenger.SendRotaMessageAsync(
            rota.Id, user.Id, model.Message, model.IncludeShifts);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Failed to queue rota emails.");
            return View(model);
        }

        SetSuccess($"Requested email for {result.RecipientCount} recipient(s) on rota '{result.RotaName}'.");
        return Redirect(Url.Action(nameof(Index), new { slug }) + "#rota-" + rota.Id.ToString("N"));
    }

    private async Task<IReadOnlyList<string>> GetRotaRecipientNamesAsync(Guid rotaId, CancellationToken ct = default)
    {
        var view = await shiftView.GetRotaAsync(rotaId, ct);
        var userIds = view.Signups
            .Where(s => s.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .Select(s => s.UserId)
            .Distinct()
            .ToList();
        if (userIds.Count == 0) return [];

        var infos = await UserService.GetUserInfosAsync(userIds, ct);
        return userIds
            .Select(id => infos.TryGetValue(id, out var u) ? u.BurnerName : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Same gate and Reply-To as the per-rota actions; the audit attaches to the Team.
    [HttpGet("Email")]
    public async Task<IActionResult> EmailTeamRotas(string slug, CancellationToken ct = default)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug, ct);
        if (teamError is not null) return teamError;

        var preview = await rotaMessenger.GetTeamRotasRecipientPreviewAsync(
            team.Id, TeamRotasAudienceFilter.Default, ct);

        var vm = new EmailTeamRotasViewModel
        {
            TeamSlug = slug,
            TeamName = team.Name,
            RotaCount = preview.RotaCount,
            RecipientCount = preview.RecipientNames.Count,
            RecipientNames = preview.RecipientNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        return View(vm);
    }

    [HttpPost("Email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailTeamRotas(string slug, EmailTeamRotasViewModel model, string? intent = null)
    {
        var (teamError, user, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        // Before any error return, against the audience currently selected, so the re-rendered form shows its list.
        var preview = await rotaMessenger.GetTeamRotasRecipientPreviewAsync(team.Id, model.Filter);
        var previewWasShown = string.Equals(
            model.PreviewedAudience, model.Filter.Key, StringComparison.Ordinal);
        model.TeamSlug = slug;
        model.TeamName = team.Name;
        // The tag helper prefers ModelState's attempted value; leaving it would re-render the stale key and turn
        // every later send back through the guard below forever.
        ModelState.Remove(nameof(EmailTeamRotasViewModel.PreviewedAudience));
        model.PreviewedAudience = model.Filter.Key;
        model.RotaCount = preview.RotaCount;
        model.RecipientCount = preview.RecipientNames.Count;
        model.RecipientNames = preview.RecipientNames
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A half-written message is not a validation failure yet.
        if (string.Equals(intent, EmailTeamRotasViewModel.RefreshIntent, StringComparison.Ordinal))
        {
            ModelState.Clear();
            return View(model);
        }

        // Without the script the audience can move unpreviewed; show this list rather than mail one never seen.
        if (!previewWasShown)
        {
            model.AudienceChanged = true;
            return View(model);
        }

        if (!ModelState.IsValid)
            return View(model);

        var result = await rotaMessenger.SendTeamRotasMessageAsync(
            team.Id, user.Id, model.Message, model.IncludeShifts, model.Filter);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Failed to queue team rota emails.");
            return View(model);
        }

        SetSuccess($"Requested email for {result.RecipientCount} recipient(s) across {result.RotaCount} rota(s) in '{result.TeamName}'.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRota(string slug, Guid rotaId)
    {
        var (teamError, user, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id);
        if (rota is null) return NotFound();

        var result = await shiftMgmt.DeleteRotaAsync(rotaId, user.Id);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Rejected deletion of {rotaId} in team {Slug}: {ErrorKey}", rotaId, slug, errorKey);
            SetError(localizer[errorKey, result.ConfirmedSignups].Value);
        }
        else
            SetSuccess(localizer["Shifts_Delete_RotaSuccess"].Value);

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Shifts/{shiftId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteShift(string slug, Guid shiftId)
    {
        var (teamError, user, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var shift = await GetShiftForTeamAsync(shiftId, team.Id);
        if (shift is null) return NotFound();

        var result = await shiftMgmt.DeleteShiftAsync(shiftId, user.Id);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Rejected deletion of {shiftId} in team {Slug}: {ErrorKey}", shiftId, slug, errorKey);
            SetError(localizer[errorKey, result.ConfirmedSignups].Value);
        }
        else
            SetSuccess(localizer["Shifts_Delete_ShiftSuccess"].Value);

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("BailRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BailRange(string slug, Guid signupBlockId, string? reason)
    {
        var (teamError, user, _) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var result = await signupService.BailRangeAsync(signupBlockId, user.Id, reason);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Rejected signup block bail {SignupBlockId} in team {Slug} for user {UserId}: {Reason}", signupBlockId, slug, user.Id, errorKey);
            SetError(localizer[errorKey].Value);
        }
        else
        {
            SetSuccess("Range bail completed.");
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("ApproveRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRange(string slug, Guid signupBlockId)
    {
        var (teamError, user, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var probe = await GetSignupBlockForTeamAsync(signupBlockId, team.Id);
        if (probe is null) return NotFound();

        var result = await signupService.ApproveRangeAsync(signupBlockId, user.Id);
        if (result.Success)
            SetSuccess(result.Warning ?? "Range approved.");
        else
            SetError(result.Error ?? "Range approval failed.");

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("RefuseRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefuseRange(string slug, Guid signupBlockId, string? reason)
    {
        var (teamError, user, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var probe = await GetSignupBlockForTeamAsync(signupBlockId, team.Id);
        if (probe is null) return NotFound();

        var result = await signupService.RefuseRangeAsync(signupBlockId, user.Id, reason);
        if (result.Success)
            SetSuccess("Range refused.");
        else
            SetError(result.Error ?? "Range refusal failed.");

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Signups/{signupId}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveSignup(string slug, Guid signupId)
    {
        var (teamError, user, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var signup = await GetSignupForTeamAsync(signupId, team.Id);
        if (signup is null) return NotFound();

        var result = await signupService.ApproveAsync(signupId, user.Id);
        if (result.Success)
        {
            SetSuccess(result.Warning ?? "Signup approved.");
        }
        else
        {
            SetError(result.Error ?? "Signup approval failed.");
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Signups/{signupId}/Refuse")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefuseSignup(string slug, Guid signupId, string? reason)
    {
        var (teamError, user, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var signup = await GetSignupForTeamAsync(signupId, team.Id);
        if (signup is null) return NotFound();

        var result = await signupService.RefuseAsync(signupId, user.Id, reason);
        if (result.Success)
        {
            SetSuccess("Signup refused.");
        }
        else
        {
            SetError(result.Error ?? "Signup refusal failed.");
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Signups/{signupId}/NoShow")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNoShow(string slug, Guid signupId)
    {
        var (teamError, user, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var signupCheck = await GetSignupForTeamAsync(signupId, team.Id);
        if (signupCheck is null) return NotFound();

        var result = await signupService.MarkNoShowAsync(signupId, user.Id);
        if (result.Success)
        {
            SetSuccess("Marked as no-show.");
        }
        else
        {
            SetError(result.Error ?? "No-show update failed.");
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Signups/{signupId}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveSignup(string slug, Guid signupId, string? reason)
    {
        var (teamError, user, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var signupCheck = await GetSignupForTeamAsync(signupId, team.Id);
        if (signupCheck is null) return NotFound();

        var result = await signupService.RemoveSignupAsync(signupId, user.Id, reason);
        if (result.Success)
            SetSuccess("Signup removed.");
        else
            SetError(result.Error ?? "Remove failed.");

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(string slug, Guid shiftId, string? query, CancellationToken ct = default)
    {
        var (teamError, _, team) = await ResolveDepartmentApprovalAsync(slug, ct);
        if (teamError is not null) return teamError;

        try
        {
            var result = await volunteerSearchBuilder.BuildForShiftAsync(
                await GetShiftForTeamAsync(shiftId, team.Id, ct),
                query,
                ShiftRoleChecks.CanViewMedical(User), ct);
            return result.Status switch
            {
                VolunteerSearchBuildStatus.EmptyQuery => Json(Array.Empty<VolunteerSearchResult>()),
                VolunteerSearchBuildStatus.NotFound => NotFound(),
                VolunteerSearchBuildStatus.Success => Json(result.Results),
                _ => throw new InvalidOperationException($"Unexpected volunteer search status '{result.Status}'.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
            return StatusCode(500, new { error = "Search failed." });
        }
    }

    [HttpPost("Voluntell")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Voluntell(string slug, Guid shiftId, Guid userId)
    {
        var (teamError, currentUser, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var shift = await GetShiftForTeamAsync(shiftId, team.Id);
        if (shift is null) return NotFound();

        var result = await signupService.VoluntellAsync(userId, shiftId, currentUser.Id);
        if (result.Success)
        {
            SetSuccess("Volunteer assigned to shift.");
        }
        else
        {
            SetError(result.Error ?? "Volunteer assignment failed.");
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("VoluntellRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VoluntellRange(string slug, Guid rotaId, [BindRequired] int startDayOffset, [BindRequired] int endDayOffset, Guid userId)
    {
        var (teamError, currentUser, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id);
        if (rota is null) return NotFound();

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed range assignment on {RotaId} by {UserId}", rotaId, currentUser.Id);
            return BadRequest(ModelState);
        }

        var result = await signupService.VoluntellRangeAsync(
            userId, rotaId, startDayOffset, endDayOffset, currentUser.Id);
        if (result.Success)
        {
            SetSuccess(result.Warning is not null
                ? $"Volunteer assigned to shift range. Note: {result.Warning}"
                : "Volunteer assigned to shift range.");
        }
        else
        {
            SetError(result.Error ?? "Range assignment failed.");
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    private async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveDepartmentManagementAsync(string slug, CancellationToken ct = default)
    {
        return await ResolveDepartmentAccessAsync(
            slug,
            (team, user) => CanManageDepartmentAsync(user, team, ct), ct);
    }

    [HttpGet("Tags/Search")]
    public async Task<IActionResult> SearchTags(string slug, string? q, CancellationToken ct = default)
    {
        var (teamError, _, _) = await ResolveDepartmentManagementAsync(slug, ct);
        if (teamError is not null) return Forbid();

        var tags = await shiftMgmt.GetTagsAsync(q, ct);

        return Json(tags
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new ShiftTagResult(t.Id, t.Name)));
    }

    [HttpPost("Tags/Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTag(string slug, string name)
    {
        var (teamError, _, _) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return Forbid();

        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            return BadRequest("Tag name is required and must be 100 characters or fewer.");

        var tag = await shiftMgmt.GetOrCreateTagAsync(name);
        return Json(new { tag.Id, tag.Name });
    }

    private async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveDepartmentApprovalAsync(string slug, CancellationToken ct = default)
    {
        return await ResolveDepartmentAccessAsync(
            slug,
            (team, user) => CanApproveDepartmentAsync(user, team, ct), ct);
    }

    private async Task<RotaInfo?> GetRotaForTeamAsync(Guid rotaId, Guid teamId, CancellationToken ct = default)
    {
        var rota = await shiftMgmt.GetRotaAsync(rotaId, ct);
        return rota is not null && rota.TeamId == teamId ? rota : null;
    }

    private async Task<ShiftWithRota?> GetShiftForTeamAsync(Guid shiftId, Guid teamId, CancellationToken ct = default)
    {
        var shift = await shiftMgmt.GetShiftByIdAsync(shiftId, ct);
        return shift is not null && shift.Rota.TeamId == teamId ? shift : null;
    }

    private async Task<ShiftSignupTeamProbe?> GetSignupForTeamAsync(Guid signupId, Guid teamId)
    {
        var signup = await signupService.GetTeamProbeAsync(signupId, ShiftSignupTeamProbeScope.Signup);
        return signup is not null && signup.TeamId == teamId ? signup : null;
    }

    private async Task<ShiftSignupTeamProbe?> GetSignupBlockForTeamAsync(Guid signupBlockId, Guid teamId)
    {
        var signup = await signupService.GetTeamProbeAsync(signupBlockId, ShiftSignupTeamProbeScope.SignupBlock);
        return signup is not null && signup.TeamId == teamId ? signup : null;
    }

    private static List<Guid> ParseTagIds(string? tagIds)
    {
        if (string.IsNullOrWhiteSpace(tagIds))
            return [];

        return tagIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => Guid.TryParse(id, out var guid) ? guid : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    private async Task<bool> CanManageDepartmentAsync(UserInfo user, TeamInfo team, CancellationToken ct = default)
    {
        // NoInfoAdmin can approve signups but not manage rotas or shifts.
        return RoleChecks.IsVolunteerManager(User) ||
               await shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id, ct);
    }

    private async Task<bool> CanApproveDepartmentAsync(UserInfo user, TeamInfo team, CancellationToken ct = default)
    {
        return ShiftRoleChecks.CanManageDepartment(User) ||
               await shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id, ct);
    }
}

internal sealed record ShiftTagResult(Guid Id, string Name);
