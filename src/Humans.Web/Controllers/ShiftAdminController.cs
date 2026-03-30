using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Teams/{slug}/Shifts")]
public class ShiftAdminController : HumansTeamControllerBase
{
    private readonly ITeamService _teamService;
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly IProfileService _profileService;
    private readonly IClock _clock;
    private readonly ILogger<ShiftAdminController> _logger;

    public ShiftAdminController(
        ITeamService teamService,
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        IProfileService profileService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<ShiftAdminController> logger)
        : base(userManager, teamService)
    {
        _teamService = teamService;
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _profileService = profileService;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(string slug)
    {
        var (teamError, user, team) = await ResolveDepartmentAccessAsync(
            slug,
            async (resolvedTeam, resolvedUser) =>
                await CanManageDepartmentAsync(resolvedUser, resolvedTeam)
                || await CanApproveDepartmentAsync(resolvedUser, resolvedTeam));
        if (teamError is not null) return teamError;

        var canManage = await CanManageDepartmentAsync(user, team);
        var canApprove = await CanApproveDepartmentAsync(user, team);

        var es = await _shiftMgmt.GetActiveAsync();
        if (es is null)
        {
            SetError("No active event settings configured.");
            return RedirectToAction("Details", "Team", new { slug });
        }

        var rotas = await _shiftMgmt.GetRotasByDepartmentAsync(team.Id, es.Id);
        var pendingSignups = new List<ShiftSignup>();
        var totalSlots = 0;
        var confirmedCount = 0;

        foreach (var rota in rotas)
        {
            foreach (var shift in rota.Shifts)
            {
                totalSlots += shift.MaxVolunteers;
                var shiftSignups = await _signupService.GetByShiftAsync(shift.Id);
                confirmedCount += shiftSignups.Count(s => s.Status == SignupStatus.Confirmed);
                pendingSignups.AddRange(shiftSignups.Where(s => s.Status == SignupStatus.Pending));
            }
        }

        var allUserIds = rotas.SelectMany(r => r.Shifts)
            .SelectMany(s => s.ShiftSignups)
            .Select(su => su.UserId)
            .Distinct()
            .ToList();

        var canViewMedical = ShiftRoleChecks.CanViewMedical(User);
        var profileDict = new Dictionary<Guid, VolunteerEventProfile>();
        foreach (var uid in allUserIds)
        {
            var profile = await _profileService.GetShiftProfileAsync(uid, includeMedical: canViewMedical);
            if (profile is not null)
            {
                profileDict[uid] = profile;
            }
        }

        var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id, team.Id);

        var allDepartments = new List<DepartmentOption>();
        if (RoleChecks.IsVolunteerManager(User))
        {
            var allTeams = await _teamService.GetAllTeamsAsync();
            allDepartments = allTeams
                .Where(t => t.ParentTeamId is null
                            && t.SystemTeamType == SystemTeamType.None
                            && t.Id != team.Id)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new DepartmentOption { TeamId = t.Id, Name = t.Name })
                .ToList();
        }

        return View(new ShiftAdminViewModel
        {
            Department = team,
            EventSettings = es,
            Rotas = rotas.ToList(),
            PendingSignups = pendingSignups,
            TotalSlots = totalSlots,
            ConfirmedCount = confirmedCount,
            CanManageShifts = canManage,
            CanApproveSignups = canApprove,
            VolunteerProfiles = profileDict,
            CanViewMedical = canViewMedical,
            StaffingData = staffingData.ToList(),
            Now = _clock.GetCurrentInstant(),
            AllDepartments = allDepartments
        });
    }

    [HttpPost("Rotas")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRota(string slug, CreateRotaModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var es = await _shiftMgmt.GetActiveAsync();
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
            CreatedAt = _clock.GetCurrentInstant()
        };

        await _shiftMgmt.CreateRotaAsync(rota);
        SetSuccess($"Rota '{model.Name}' created.");
        return Redirect(Url.Action(nameof(Index), new { slug }) + "#rota-" + rota.Id.ToString("N"));
    }

    [HttpPost("Rotas/{rotaId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRota(string slug, Guid rotaId, EditRotaModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota is null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        rota.Name = model.Name;
        rota.Description = model.Description;
        rota.Priority = model.Priority;
        rota.Policy = model.Policy;
        rota.Period = model.Period;
        rota.PracticalInfo = model.PracticalInfo;

        await _shiftMgmt.UpdateRotaAsync(rota);
        SetSuccess($"Rota '{model.Name}' updated.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/ConfigureStaffing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureStaffing(string slug, Guid rotaId, StaffingGridModel model)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota is null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        var dailyStaffing = model.Days.ToDictionary(
            d => d.DayOffset,
            d => (d.MinVolunteers, d.MaxVolunteers));

        try
        {
            await _shiftMgmt.CreateBuildStrikeShiftsAsync(rotaId, dailyStaffing);
            SetSuccess($"Created {model.Days.Count} shifts for '{rota.Name}'.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to configure staffing for rota {RotaId} in team {TeamId}", rotaId, team.Id);
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

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota is null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        var timeSlots = new List<(LocalTime StartTime, double DurationHours)>();
        foreach (var slot in model.TimeSlots)
        {
            if (!slot.StartTime.TryParseInvariantLocalTime(out var parsed))
            {
                SetError($"Invalid start time: {slot.StartTime}");
                return RedirectToAction(nameof(Index), new { slug });
            }

            timeSlots.Add((parsed, slot.DurationHours));
        }

        try
        {
            await _shiftMgmt.GenerateEventShiftsAsync(
                rotaId,
                model.StartDayOffset,
                model.EndDayOffset,
                timeSlots,
                model.MinVolunteers,
                model.MaxVolunteers);
            var shiftCount = Math.Max(0, model.EndDayOffset - model.StartDayOffset + 1) * model.TimeSlots.Count;
            SetSuccess($"Generated {shiftCount} shifts for '{rota.Name}'.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to generate shifts for rota {RotaId} in team {TeamId}", rotaId, team.Id);
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

        var rota = await _shiftMgmt.GetRotaByIdAsync(model.RotaId);
        if (rota is null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        if (!model.StartTime.TryParseInvariantLocalTime(out var parsedTime))
        {
            SetError("Invalid start time format.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var es = rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
        if (es is not null)
        {
            var (periodStart, periodEnd) = rota.Period switch
            {
                RotaPeriod.Build => (es.BuildStartOffset, -1),
                RotaPeriod.Event => (0, es.EventEndOffset),
                RotaPeriod.Strike => (es.EventEndOffset + 1, es.StrikeEndOffset),
                _ => (es.BuildStartOffset, es.StrikeEndOffset)
            };
            if (model.DayOffset < periodStart || model.DayOffset > periodEnd)
            {
                SetError("Shift date must fall within the rota's period.");
                return RedirectToAction(nameof(Index), new { slug });
            }
        }

        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = model.RotaId,
            Description = model.Description,
            DayOffset = model.DayOffset,
            StartTime = parsedTime,
            Duration = Duration.FromHours(model.DurationHours),
            MinVolunteers = model.MinVolunteers,
            MaxVolunteers = model.MaxVolunteers,
            AdminOnly = model.AdminOnly,
            CreatedAt = _clock.GetCurrentInstant()
        };

        try
        {
            await _shiftMgmt.CreateShiftAsync(shift);
            SetSuccess("Shift created.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create shift for rota {RotaId} in team {TeamId}", model.RotaId, team.Id);
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

        var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
        if (shift is null) return NotFound();
        if (shift.Rota.TeamId != team.Id) return NotFound();

        if (!model.StartTime.TryParseInvariantLocalTime(out var parsedTime))
        {
            SetError("Invalid start time format.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        var rota = shift.Rota;
        var editEs = rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
        if (editEs is not null)
        {
            var (periodStart, periodEnd) = rota.Period switch
            {
                RotaPeriod.Build => (editEs.BuildStartOffset, -1),
                RotaPeriod.Event => (0, editEs.EventEndOffset),
                RotaPeriod.Strike => (editEs.EventEndOffset + 1, editEs.StrikeEndOffset),
                _ => (editEs.BuildStartOffset, editEs.StrikeEndOffset)
            };
            if (model.DayOffset < periodStart || model.DayOffset > periodEnd)
            {
                SetError("Shift date must fall within the rota's period.");
                return RedirectToAction(nameof(Index), new { slug });
            }
        }

        shift.Description = model.Description;
        shift.DayOffset = model.DayOffset;
        shift.StartTime = parsedTime;
        shift.Duration = Duration.FromHours(model.DurationHours);
        shift.MinVolunteers = model.MinVolunteers;
        shift.MaxVolunteers = model.MaxVolunteers;
        shift.AdminOnly = model.AdminOnly;

        await _shiftMgmt.UpdateShiftAsync(shift);
        SetSuccess("Shift updated.");
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/ToggleVisibility")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleVisibility(string slug, Guid rotaId)
    {
        var (teamError, _, team) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota is null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        rota.IsVisibleToVolunteers = !rota.IsVisibleToVolunteers;
        await _shiftMgmt.UpdateRotaAsync(rota);

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

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota is null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        var targetTeam = await _teamService.GetTeamByIdAsync(model.TargetTeamId);
        if (targetTeam is null)
        {
            SetError("Target team not found.");
            return RedirectToAction(nameof(Index), new { slug });
        }

        try
        {
            await _shiftMgmt.MoveRotaToTeamAsync(rotaId, model.TargetTeamId, user.Id, user.DisplayName);
            SetSuccess($"Rota '{rota.Name}' moved to {targetTeam.Name}.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to move rota {RotaId} to team {TargetTeamId}", rotaId, model.TargetTeamId);
            SetError(ex.Message);
            return RedirectToAction(nameof(Index), new { slug });
        }

        return RedirectToAction(nameof(Index), new { slug = targetTeam.Slug });
    }

    [HttpPost("Rotas/{rotaId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRota(string slug, Guid rotaId)
    {
        var (teamError, _, _) = await ResolveDepartmentManagementAsync(slug);
        if (teamError is not null) return teamError;

        try
        {
            await _shiftMgmt.DeleteRotaAsync(rotaId);
            SetSuccess("Rota deleted.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to delete rota {RotaId} in team {Slug}", rotaId, slug);
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
            await _shiftMgmt.DeleteShiftAsync(shiftId);
            SetSuccess("Shift deleted.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to delete shift {ShiftId} in team {Slug}", shiftId, slug);
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
            await _signupService.BailRangeAsync(signupBlockId, user.Id, reason);
            SetSuccess("Range bail completed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to bail signup block {SignupBlockId} in team {Slug}", signupBlockId, slug);
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

        var probe = await _signupService.GetByBlockIdFirstAsync(signupBlockId);
        if (probe is null || probe.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.ApproveRangeAsync(signupBlockId, user.Id);
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

        var probe = await _signupService.GetByBlockIdFirstAsync(signupBlockId);
        if (probe is null || probe.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.RefuseRangeAsync(signupBlockId, user.Id, reason);
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

        var signup = await _signupService.GetByIdAsync(signupId);
        if (signup is null) return NotFound();
        if (signup.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.ApproveAsync(signupId, user.Id);
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

        var signup = await _signupService.GetByIdAsync(signupId);
        if (signup is null) return NotFound();
        if (signup.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.RefuseAsync(signupId, user.Id, reason);
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

        var signupCheck = await _signupService.GetByIdAsync(signupId);
        if (signupCheck is null) return NotFound();
        if (signupCheck.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.MarkNoShowAsync(signupId, user.Id);
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

        var signupCheck = await _signupService.GetByIdAsync(signupId);
        if (signupCheck is null) return NotFound();
        if (signupCheck.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.RemoveSignupAsync(signupId, user.Id, reason);
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

        if (!query.HasSearchTerm())
        {
            return Json(Array.Empty<VolunteerSearchResult>());
        }

        try
        {
            var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
            if (shift is null) return NotFound();
            if (shift.Rota.TeamId != team.Id) return NotFound();

            var es = shift.Rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
            if (es is null) return NotFound();

            var results = await ShiftVolunteerSearchBuilder.BuildAsync(
                shift,
                query,
                es,
                ShiftRoleChecks.CanViewMedical(User),
                UserManager,
                _profileService,
                _signupService,
                _availabilityService);
            return Json(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Volunteer search failed for shift {ShiftId}, query '{Query}'", shiftId, query);
            return StatusCode(500, new { error = "Search failed." });
        }
    }

    [HttpPost("Voluntell")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Voluntell(string slug, Guid shiftId, Guid userId)
    {
        var (teamError, currentUser, team) = await ResolveDepartmentApprovalAsync(slug);
        if (teamError is not null) return teamError;

        var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
        if (shift is null) return NotFound();
        if (shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.VoluntellAsync(userId, shiftId, currentUser.Id);
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

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota is null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.VoluntellRangeAsync(userId, rotaId, startDayOffset, endDayOffset, currentUser.Id);
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

    private async Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveDepartmentManagementAsync(string slug)
    {
        return await ResolveDepartmentAccessAsync(
            slug,
            (team, user) => CanManageDepartmentAsync(user, team));
    }

    private async Task<(IActionResult? ErrorResult, User User, Team Team)> ResolveDepartmentApprovalAsync(string slug)
    {
        return await ResolveDepartmentAccessAsync(
            slug,
            (team, user) => CanApproveDepartmentAsync(user, team));
    }

    private async Task<bool> CanManageDepartmentAsync(User user, Team team)
    {
        // Management: Admin + VolunteerCoordinator + dept coordinator
        // Explicitly excludes NoInfoAdmin — they can approve signups but not manage rotas/shifts
        return RoleChecks.IsVolunteerManager(User) ||
               await _shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id);
    }

    private async Task<bool> CanApproveDepartmentAsync(User user, Team team)
    {
        // Approval: Admin + NoInfoAdmin + VolunteerCoordinator + dept coordinator
        return ShiftRoleChecks.CanManageDepartment(User) ||
               await _shiftMgmt.IsDeptCoordinatorAsync(user.Id, team.Id);
    }
}
