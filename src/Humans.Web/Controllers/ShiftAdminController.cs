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
public class ShiftAdminController : HumansControllerBase
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
        : base(userManager)
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
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();

        var canManage = await CanManageAsync(userId.Value, team.Id);
        var canApprove = await CanApproveAsync(userId.Value, team.Id);
        if (!canManage && !canApprove) return Forbid();

        var es = await _shiftMgmt.GetActiveAsync();
        if (es == null)
        {
            TempData["ErrorMessage"] = "No active event settings configured.";
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

        // Batch-load volunteer event profiles for signup display
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
            if (profile != null)
                profileDict[uid] = profile;
        }

        var staffingData = await _shiftMgmt.GetStaffingDataAsync(es.Id, team.Id);

        var model = new ShiftAdminViewModel
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
            Now = _clock.GetCurrentInstant()
        };

        return View(model);
    }

    [HttpPost("Rotas")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRota(string slug, CreateRotaModel model)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        var es = await _shiftMgmt.GetActiveAsync();
        if (es == null) return BadRequest("No active event.");

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Please fix the errors below.";
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
        TempData["SuccessMessage"] = $"Rota '{model.Name}' created.";
        return Redirect(Url.Action(nameof(Index), new { slug }) + "#rota-" + rota.Id.ToString("N"));
    }

    [HttpPost("Rotas/{rotaId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRota(string slug, Guid rotaId, EditRotaModel model)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota == null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        rota.Name = model.Name;
        rota.Description = model.Description;
        rota.Priority = model.Priority;
        rota.Policy = model.Policy;
        rota.Period = model.Period;
        rota.PracticalInfo = model.PracticalInfo;

        await _shiftMgmt.UpdateRotaAsync(rota);
        TempData["SuccessMessage"] = $"Rota '{model.Name}' updated.";
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/ConfigureStaffing")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfigureStaffing(string slug, Guid rotaId, StaffingGridModel model)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota == null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        var dailyStaffing = model.Days.ToDictionary(
            d => d.DayOffset,
            d => (d.MinVolunteers, d.MaxVolunteers));

        try
        {
            await _shiftMgmt.CreateBuildStrikeShiftsAsync(rotaId, dailyStaffing);
            TempData["SuccessMessage"] = $"Created {model.Days.Count} shifts for '{rota.Name}'.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/GenerateShifts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateShifts(string slug, Guid rotaId, GenerateEventShiftsModel model)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        var rota = await _shiftMgmt.GetRotaByIdAsync(rotaId);
        if (rota == null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        var timeSlots = new List<(LocalTime StartTime, double DurationHours)>();
        foreach (var slot in model.TimeSlots)
        {
            if (!slot.StartTime.TryParseInvariantLocalTime(out var parsed))
            {
                TempData["ErrorMessage"] = $"Invalid start time: {slot.StartTime}";
                return RedirectToAction(nameof(Index), new { slug });
            }
            timeSlots.Add((parsed, slot.DurationHours));
        }

        try
        {
            await _shiftMgmt.GenerateEventShiftsAsync(rotaId, model.StartDayOffset, model.EndDayOffset,
                timeSlots, model.MinVolunteers, model.MaxVolunteers);
            var shiftCount = Math.Max(0, model.EndDayOffset - model.StartDayOffset + 1) * model.TimeSlots.Count;
            TempData["SuccessMessage"] = $"Generated {shiftCount} shifts for '{rota.Name}'.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Shifts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateShift(string slug, CreateShiftModel model)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        if (!ModelState.IsValid)
        {
            TempData["ErrorMessage"] = "Please fix the errors below.";
            return RedirectToAction(nameof(Index), new { slug });
        }

        // Verify rota belongs to this department
        var rota = await _shiftMgmt.GetRotaByIdAsync(model.RotaId);
        if (rota == null) return NotFound();
        if (rota.TeamId != team.Id) return NotFound();

        if (!model.StartTime.TryParseInvariantLocalTime(out var parsedTime))
        {
            TempData["ErrorMessage"] = "Invalid start time format.";
            return RedirectToAction(nameof(Index), new { slug });
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
            TempData["SuccessMessage"] = "Shift created.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Shifts/{shiftId}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditShift(string slug, Guid shiftId, EditShiftModel model)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
        if (shift == null) return NotFound();
        if (shift.Rota.TeamId != team.Id) return NotFound();

        if (!model.StartTime.TryParseInvariantLocalTime(out var parsedTime))
        {
            TempData["ErrorMessage"] = "Invalid start time format.";
            return RedirectToAction(nameof(Index), new { slug });
        }

        shift.Description = model.Description;
        shift.DayOffset = model.DayOffset;
        shift.StartTime = parsedTime;
        shift.Duration = Duration.FromHours(model.DurationHours);
        shift.MinVolunteers = model.MinVolunteers;
        shift.MaxVolunteers = model.MaxVolunteers;
        shift.AdminOnly = model.AdminOnly;

        await _shiftMgmt.UpdateShiftAsync(shift);
        TempData["SuccessMessage"] = "Shift updated.";
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Rotas/{rotaId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRota(string slug, Guid rotaId)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        try
        {
            await _shiftMgmt.DeleteRotaAsync(rotaId);
            TempData["SuccessMessage"] = "Rota deleted.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Shifts/{shiftId}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteShift(string slug, Guid shiftId)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanManageAsync(userId.Value, team.Id)) return Forbid();

        try
        {
            await _shiftMgmt.DeleteShiftAsync(shiftId);
            TempData["SuccessMessage"] = "Shift deleted.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("BailRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BailRange(string slug, Guid signupBlockId, string? reason)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanApproveAsync(userId.Value, team.Id)) return Forbid();

        try
        {
            await _signupService.BailRangeAsync(signupBlockId, userId.Value, reason);
            TempData["SuccessMessage"] = "Range bail completed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Signups/{signupId}/Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveSignup(string slug, Guid signupId)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanApproveAsync(userId.Value, team.Id)) return Forbid();

        var signup = await _signupService.GetByIdAsync(signupId);
        if (signup == null) return NotFound();
        if (signup.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.ApproveAsync(signupId, userId.Value);
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] =
            result.Success ? (result.Warning ?? "Signup approved.") : result.Error;

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Signups/{signupId}/Refuse")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefuseSignup(string slug, Guid signupId, string? reason)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanApproveAsync(userId.Value, team.Id)) return Forbid();

        var signup = await _signupService.GetByIdAsync(signupId);
        if (signup == null) return NotFound();
        if (signup.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.RefuseAsync(signupId, userId.Value, reason);
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] =
            result.Success ? "Signup refused." : result.Error;

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpPost("Signups/{signupId}/NoShow")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkNoShow(string slug, Guid signupId)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanApproveAsync(userId.Value, team.Id)) return Forbid();

        var signupCheck = await _signupService.GetByIdAsync(signupId);
        if (signupCheck == null) return NotFound();
        if (signupCheck.Shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.MarkNoShowAsync(signupId, userId.Value);
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] =
            result.Success ? "Marked as no-show." : result.Error;

        return RedirectToAction(nameof(Index), new { slug });
    }

    [HttpGet("SearchVolunteers")]
    public async Task<IActionResult> SearchVolunteers(string slug, Guid shiftId, string? query)
    {
        var (team, userId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || userId == null) return NotFound();
        if (!await CanApproveAsync(userId.Value, team.Id)) return Forbid();

        if (!query.HasSearchTerm())
            return Json(Array.Empty<VolunteerSearchResult>());

        try
        {
            var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
            if (shift == null) return NotFound();
            if (shift.Rota.TeamId != team.Id) return NotFound();

            var es = shift.Rota.EventSettings ?? await _shiftMgmt.GetActiveAsync();
            if (es == null) return NotFound();

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
        var (team, currentUserId) = await ResolveTeamAndUserAsync(slug);
        if (team == null || currentUserId == null) return NotFound();
        if (!await CanApproveAsync(currentUserId.Value, team.Id)) return Forbid();

        var shift = await _shiftMgmt.GetShiftByIdAsync(shiftId);
        if (shift == null) return NotFound();
        if (shift.Rota.TeamId != team.Id) return NotFound();

        var result = await _signupService.VoluntellAsync(userId, shiftId, currentUserId.Value);
        TempData[result.Success ? "SuccessMessage" : "ErrorMessage"] =
            result.Success ? "Volunteer assigned to shift." : result.Error;

        return RedirectToAction(nameof(Index), new { slug });
    }

    private async Task<bool> CanManageAsync(Guid userId, Guid teamId)
    {
        // Claims-first for global roles; DB only for team-specific coordinator check
        return RoleChecks.IsAdmin(User) ||
               User.IsInRole(RoleNames.VolunteerCoordinator) ||
               await _shiftMgmt.IsDeptCoordinatorAsync(userId, teamId);
    }

    private async Task<bool> CanApproveAsync(Guid userId, Guid teamId)
    {
        // Claims-first for global roles; DB only for team-specific coordinator check
        return ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
               User.IsInRole(RoleNames.VolunteerCoordinator) ||
               await _shiftMgmt.IsDeptCoordinatorAsync(userId, teamId);
    }

    private async Task<(Team? Team, Guid? UserId)> ResolveTeamAndUserAsync(string slug)
    {
        var user = await GetCurrentUserAsync();
        if (user == null) return (null, null);

        var team = await _teamService.GetTeamBySlugAsync(slug);
        if (team == null || team.ParentTeamId != null || team.SystemTeamType != SystemTeamType.None)
            return (null, null);

        return (team, user.Id);
    }
}
