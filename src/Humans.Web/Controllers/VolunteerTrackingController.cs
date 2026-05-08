using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime.Text;

namespace Humans.Web.Controllers;

/// <summary>
/// Coordinator-facing tracking page: the main "fell off" cohort heatmap and
/// the declared-but-unbooked cohort heatmap, plus mutating actions for
/// camp-setup and per-day blocks. Class-level
/// <c>[Authorize(Policy = ShiftDashboardAccess)]</c> gates read; mutating
/// actions add <c>[Authorize(Policy = VolunteerTrackingWrite)]</c>. Display
/// sorting happens here (per
/// <c>memory/architecture/display-sort-in-controllers.md</c>) — the service
/// returns unsorted cohorts.
/// </summary>
[Route("ShiftDashboard/[controller]")]
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
public sealed class VolunteerTrackingController : HumansControllerBase
{
    private readonly IVolunteerTrackingService _service;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLogService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public VolunteerTrackingController(
        IVolunteerTrackingService service,
        IUserService userService,
        IAuditLogService auditLogService,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _service = service;
        _userService = userService;
        _auditLogService = auditLogService;
        _localizer = localizer;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        bool hideNoGaps = false,
        bool hideCampSetup = false,
        bool hideUnbookedSection = false,
        CancellationToken ct = default)
    {
        var data = await _service.GetTrackingDataAsync(ct);
        if (!data.HasActiveEvent)
        {
            return View(VolunteerTrackingPageViewModel.Empty);
        }

        var displayUserIds = data.MainCohort.Select(r => r.UserId)
            .Concat(data.UnbookedCohort.Select(r => r.UserId))
            .Distinct()
            .ToArray();
        var users = await _userService.GetByIdsAsync(displayUserIds, ct);

        // Display sort in the controller (presentation concern).
        var mainSorted = data.MainCohort
            .Where(r => !hideNoGaps || r.GapCount > 0)
            .Where(r => !hideCampSetup || r.BarrioSetupStartDate is null)
            .OrderByDescending(r => r.GapCount)
            .ThenBy(r => r.LastEligibleSignupOffset)
            .ThenBy(r => DisplayName(users, r.UserId), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unbookedSorted = hideUnbookedSection
            ? new List<VolunteerCohortRow>()
            : data.UnbookedCohort
                .OrderByDescending(r => r.UnbookedCount)
                .ThenBy(r => r.FirstAvailableDay)
                .ThenBy(r => DisplayName(users, r.UserId), StringComparer.OrdinalIgnoreCase)
                .ToList();

        var model = new VolunteerTrackingPageViewModel(
            data.BuildStartOffset,
            data.GateOpeningDate,
            mainSorted,
            unbookedSorted,
            users,
            hideNoGaps,
            hideCampSetup,
            hideUnbookedSection);

        return View(model);
    }

    [HttpPost("SetCampSetup")]
    [Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCampSetup(SetCampSetupForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(_localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var parseResult = LocalDatePattern.Iso.Parse(form.Date);
        if (!parseResult.Success)
        {
            SetError(_localizer["VolTrack_Err_BadDate"]);
            return RedirectToAction(nameof(Index));
        }
        var parsed = parseResult.Value;

        var current = await GetCurrentUserAsync();
        if (current is null) return Forbid();

        var result = await _service.SetCampSetupAsync(form.UserId, parsed, form.Notes, current.Id, ct);
        if (!result.Ok)
        {
            SetError(_localizer[result.ErrorMessageKey ?? "VolTrack_Err_Unknown"]);
            return RedirectToAction(nameof(Index));
        }

        await _auditLogService.LogAsync(
            AuditAction.VolunteerCampSetupSet,
            nameof(VolunteerBuildStatus),
            form.UserId,
            $"BarrioSetupStartDate set to {form.Date}; notes={form.Notes ?? "—"}",
            current.Id);

        SetSuccess(_localizer["VolTrack_Msg_CampSetupSaved"]);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ClearCampSetup")]
    [Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearCampSetup(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
        {
            SetError(_localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var current = await GetCurrentUserAsync();
        if (current is null) return Forbid();

        await _service.ClearCampSetupAsync(userId, current.Id, ct);

        await _auditLogService.LogAsync(
            AuditAction.VolunteerCampSetupCleared,
            nameof(VolunteerBuildStatus),
            userId,
            "BarrioSetupStartDate cleared",
            current.Id);

        SetSuccess(_localizer["VolTrack_Msg_CampSetupCleared"]);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetBlock")]
    [Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetBlock(SetBlockForm form, CancellationToken ct)
    {
        var current = await GetCurrentUserAsync();
        if (current is null) return Forbid();

        var result = await _service.SetBlockAsync(form.UserId, form.DayOffset, form.Block, current.Id, ct);
        if (!result.Ok) return SetBlockError(result.ErrorMessageKey);
        if (result.Changed) await EmitSetBlockAuditAsync(form, current.Id);

        SetSuccess(_localizer[SetBlockSuccessKey(form.Block)]);
        return RedirectToAction(nameof(Index));
    }

    private IActionResult SetBlockError(string? errorMessageKey)
    {
        SetError(_localizer[errorMessageKey ?? "VolTrack_Err_Unknown"]);
        return BadRequest();
    }

    private Task EmitSetBlockAuditAsync(SetBlockForm form, Guid actorUserId) =>
        _auditLogService.LogAsync(
            form.Block ? AuditAction.VolunteerDayBlocked : AuditAction.VolunteerDayUnblocked,
            nameof(VolunteerBuildStatus),
            form.UserId,
            $"DayOffset={form.DayOffset}; by coordinator",
            actorUserId);

    private static string SetBlockSuccessKey(bool block) =>
        block ? "VolTrack_Msg_DayBlocked" : "VolTrack_Msg_DayUnblocked";

    private static string DisplayName(IReadOnlyDictionary<Guid, User> users, Guid id)
        => users.TryGetValue(id, out var u) ? (u.DisplayName ?? "") : "";
}
