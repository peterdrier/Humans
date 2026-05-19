using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

using Humans.Application.Interfaces.Users;
using Humans.Application;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams/{slug}/Shifts")]
public class ShiftAdminController(
    ITeamService teamService,
    IShiftManagementService shiftMgmt,
    IShiftSignupService signupService,
    IShiftView shiftView,
    IGeneralAvailabilityService availabilityService,
    IUserService userService,
    IAuthorizationService authorizationService,
    IClock clock,
    ShiftAdminPageBuilder pageBuilder,
    IRotaCoordinatorMessageService rotaMessenger,
    ILogger<ShiftAdminController> logger) : HumansTeamControllerBase(userService, teamService, authorizationService)
{
    private readonly ITeamService _teamService = teamService;

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug, bool incompleteOnboarding = false)
    {
        var (teamError, user, team) = await ResolveDepartmentAccessAsync(
            slug,
            async (resolvedTeam, resolvedUser) =>
                await CanManageDepartmentAsync(resolvedUser, resolvedTeam)
                || await CanApproveDepartmentAsync(resolvedUser, resolvedTeam));
        if (teamError is not null) return teamError;

        var canManage = await CanManageDepartmentAsync(user, team);
        var canApprove = await CanApproveDepartmentAsync(user, team);
        var es = await shiftMgmt.GetActiveAsync();
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction(nameof(TeamController.Details), "Team", new { slug });
        }

        var teamEntity = await _teamService.GetTeamByIdAsync(team.Id);
        if (teamEntity is null) return NotFound();

        var model = await pageBuilder.BuildAsync(new ShiftAdminPageRequest(
            teamEntity,
            es,
            canManage,
            canApprove,
            ShiftRoleChecks.CanViewMedical(User),
            incompleteOnboarding,
            clock.GetCurrentInstant()));

        return View(model);
    }

    [HttpPost("Rotas")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRota(string slug, CreateRotaModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var es = await shiftMgmt.GetActiveAsync();
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

        await shiftMgmt.CreateRotaAsync(rota);

        if (!string.IsNullOrWhiteSpace(model.TagIds))
        {
            var tagIdList = ParseTagIds(model.TagIds);
            await shiftMgmt.SetRotaTagsAsync(rota.Id, tagIdList);
        }

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

        rota.Name = model.Name;
        rota.Description = model.Description;
        rota.Priority = model.Priority;
        rota.Policy = model.Policy;
        rota.Period = model.Period;
        rota.PracticalInfo = model.PracticalInfo;

        await shiftMgmt.UpdateRotaAsync(rota);

        var tagIdList = ParseTagIds(model.TagIds);
        await shiftMgmt.SetRotaTagsAsync(rota.Id, tagIdList);

        SetSuccess($"Rota '{model.Name}' updated.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/ConfigureStaffing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureStaffing(string slug, Guid rotaId, StaffingGridModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

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
            logger.LogWarning(ex, "Failed to configure staffing for rota {RotaId} in team {TeamId}", rotaId, team.Id);
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

        if (!model.StartTime.TryParseInvariantLocalTime(out var parsedTime))
        {
            SetError("Invalid start time format.");
            return RedirectToAction(nameof(Index), new { slug });
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

        rota.IsVisibleToVolunteers = !rota.IsVisibleToVolunteers;
        await shiftMgmt.UpdateRotaAsync(rota);

        var label = rota.IsVisibleToVolunteers ? "visible to" : "hidden from";
        SetSuccess($"Rota '{rota.Name}' is now {label} volunteers.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/Move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveRota(string slug, Guid rotaId, MoveRotaModel model)
    {
        if (!RoleChecks.IsVolunteerManager(User))
            return Forbid();

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

    // see nobodies-collective/Humans#732 — coordinator "email a rota" (mgmt scope, excludes NoInfoAdmin).
    [HttpGet("Rotas/{rotaId}/Email")]
    public async Task<IActionResult> EmailRota(string slug, Guid rotaId)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id);
        if (rota is null) return NotFound();

        var recipientNames = await GetRotaRecipientNamesAsync(rota.Id);

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

        // Repopulate display fields before any return-with-error path.
        var recipientNames = await GetRotaRecipientNamesAsync(rota.Id);
        model.RotaId = rota.Id;
        model.RotaName = rota.Name;
        model.TeamSlug = slug;
        model.RecipientCount = recipientNames.Count;
        model.RecipientNames = recipientNames;

        if (!ModelState.IsValid)
            return View(model);

        var result = await rotaMessenger.SendRotaMessageAsync(rota.Id, user.Id, model.Message);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Failed to queue rota emails.");
            return View(model);
        }

        SetSuccess($"Queued {result.RecipientCount} email(s) to recipients on rota '{result.RotaName}'.");
        return Redirect(Url.Action(nameof(Index), new { slug }) + "#rota-" + rota.Id.ToString("N"));
    }

    private async Task<IReadOnlyList<string>> GetRotaRecipientNamesAsync(Guid rotaId)
    {
        var view = await shiftView.GetRotaAsync(rotaId);
        var userIds = view.Signups
            .Where(s => s.Status is SignupStatus.Pending or SignupStatus.Confirmed)
            .Select(s => s.UserId)
            .Distinct()
            .ToList();
        if (userIds.Count == 0) return [];

        var infos = await UserService.GetUserInfosAsync(userIds);
        return userIds
            .Select(id => infos.TryGetValue(id, out var u) ? u.BurnerName : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [HttpPost("Rotas/{rotaId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRota(string slug, Guid rotaId)
    {
        var (teamError, _, _) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        try
        {
            await shiftMgmt.DeleteRotaAsync(rotaId);
            SetSuccess("Rota deleted.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to delete rota {RotaId} in team {Slug}", rotaId, slug);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Shifts/{shiftId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteShift(string slug, Guid shiftId)
    {
        var (teamError, _, _) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        try
        {
            await shiftMgmt.DeleteShiftAsync(shiftId);
            SetSuccess("Shift deleted.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Rejected shift delete for shift {ShiftId} in team {Slug}: {Reason}", shiftId, slug, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("BailRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BailRange(string slug, Guid signupBlockId, string? reason)
    {
        var (teamError, user, _) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        try
        {
            await signupService.BailRangeAsync(signupBlockId, user.Id, reason);
            SetSuccess("Range bail completed.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to bail signup block {SignupBlockId} in team {Slug}", signupBlockId, slug);
            SetError(ex.Message);
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
    public async Task<IActionResult> SearchVolunteers(string slug, Guid shiftId, string? query)
    {
        var (teamError, _, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        try
        {
            var result = await ShiftVolunteerSearchBuilder.BuildForShiftAsync(
                await GetShiftForTeamAsync(shiftId, team.Id),
                query,
                shiftMgmt.GetActiveAsync,
                ShiftRoleChecks.CanViewMedical(User),
                UserService,
                shiftView,
                signupService,
                availabilityService);
            return ToVolunteerSearchActionResult(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
            return StatusCode(500, new { error = "Search failed." });
        }
    }

    private IActionResult ToVolunteerSearchActionResult(VolunteerSearchBuildResult result) =>
        result.Status switch
        {
            VolunteerSearchBuildStatus.EmptyQuery => Json(Array.Empty<VolunteerSearchResult>()),
            VolunteerSearchBuildStatus.NotFound => NotFound(),
            VolunteerSearchBuildStatus.Success => Json(result.Results),
            _ => throw new InvalidOperationException($"Unexpected volunteer search status '{result.Status}'.")
        };

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
    public async Task<IActionResult> VoluntellRange(string slug, Guid rotaId, int startDayOffset, int endDayOffset, Guid userId)
    {
        var (teamError, currentUser, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await GetRotaForTeamAsync(rotaId, team.Id);
        if (rota is null) return NotFound();

        var result = await signupService.VoluntellRangeAsync(userId, rotaId, startDayOffset, endDayOffset, currentUser.Id);
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

    private async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveDepartmentManagementAsync(string slug)
    {
        return await ResolveDepartmentAccessAsync(
            slug,
            (team, user) => CanManageDepartmentAsync(user, team));
    }

    [HttpGet("Tags/Search")]
    public async Task<IActionResult> SearchTags(string slug, string? q)
    {
        var (teamError, _, _) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return Forbid();

        var tags = await shiftMgmt.GetTagsAsync(q);

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

    private async Task<(IActionResult? ErrorResult, UserInfo User, TeamInfo Team)> ResolveDepartmentApprovalAsync(string slug)
    {
        return await ResolveDepartmentAccessAsync(
            slug,
            (team, user) => CanApproveDepartmentAsync(user, team));
    }

    private async Task<Rota?> GetRotaForTeamAsync(Guid rotaId, Guid teamId)
    {
        var rota = await shiftMgmt.GetRotaByIdAsync(rotaId);
        return rota is not null && rota.TeamId == teamId ? rota : null;
    }

    private async Task<Shift?> GetShiftForTeamAsync(Guid shiftId, Guid teamId)
    {
        var shift = await shiftMgmt.GetShiftByIdAsync(shiftId);
        return shift is not null && shift.Rota.TeamId == teamId ? shift : null;
    }

    private async Task<ShiftSignupTeamProbe?> GetSignupForTeamAsync(Guid signupId, Guid teamId)
    {
        var signup = await signupService.GetByIdAsync(signupId);
        return signup is not null && signup.TeamId == teamId ? signup : null;
    }

    private async Task<ShiftSignupTeamProbe?> GetSignupBlockForTeamAsync(Guid signupBlockId, Guid teamId)
    {
        var signup = await signupService.GetByBlockIdFirstAsync(signupBlockId);
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

    private async Task<bool> CanManageDepartmentAsync(UserInfo user, TeamInfo team)
    {
        // Management: Admin + VolunteerCoordinator + dept coordinator
        // Explicitly excludes NoInfoAdmin — they can approve signups but not manage rotas/shifts
        return RoleChecks.IsVolunteerManager(User) ||
               await shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id);
    }

    private async Task<bool> CanApproveDepartmentAsync(UserInfo user, TeamInfo team)
    {
        // Approval: Admin + NoInfoAdmin + VolunteerCoordinator + dept coordinator
        return ShiftRoleChecks.CanManageDepartment(User) ||
               await shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id);
    }
}

public sealed record ShiftTagResult(Guid Id, string Name);
