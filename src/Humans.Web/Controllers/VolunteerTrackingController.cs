using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
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
[Route("Shifts/Dashboard/VolunteerTracking")]
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
public sealed class VolunteerTrackingController : HumansControllerBase
{
    private readonly IVolunteerTrackingService _service;
    private readonly IProfileService _profileService;
    private readonly IAuditLogService _auditLogService;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public VolunteerTrackingController(
        IVolunteerTrackingService service,
        IProfileService profileService,
        IAuditLogService auditLogService,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer)
        : base(userManager)
    {
        _service = service;
        _profileService = profileService;
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

        // Resolve BurnerName-aware display names for the sort key. Per
        // memory/architecture/burnername-is-the-display-name.md, never read
        // user.DisplayName for rendering when a Profile exists. The cell-
        // rendering path uses <vc:human> which fetches its own FullProfile;
        // here we only need a stable string per user-id for the row sort.
        var displayUserIds = data.MainCohort.Select(r => r.UserId)
            .Concat(data.UnbookedCohort.Select(r => r.UserId))
            .Distinct()
            .ToArray();
        var nameByUserId = new Dictionary<Guid, string>(displayUserIds.Length);
        foreach (var uid in displayUserIds)
        {
            var fp = await _profileService.GetFullProfileAsync(uid, ct);
            nameByUserId[uid] = fp?.DisplayName ?? "";
        }

        // Display sort in the controller (presentation concern).
        var mainSorted = data.MainCohort
            .Where(r => !hideNoGaps || r.GapCount > 0)
            .Where(r => !hideCampSetup || r.BarrioSetupStartDate is null)
            .OrderByDescending(r => r.GapCount)
            .ThenBy(r => r.LastEligibleSignupOffset)
            .ThenBy(r => nameByUserId.GetValueOrDefault(r.UserId, ""), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unbookedSorted = hideUnbookedSection
            ? new List<VolunteerCohortRow>()
            : data.UnbookedCohort
                .OrderByDescending(r => r.UnbookedCount)
                .ThenBy(r => r.FirstAvailableDay)
                .ThenBy(r => nameByUserId.GetValueOrDefault(r.UserId, ""), StringComparer.OrdinalIgnoreCase)
                .ToList();

        var model = new VolunteerTrackingPageViewModel(
            data.BuildStartOffset,
            data.GateOpeningDate,
            data.Today,
            mainSorted,
            unbookedSorted,
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

        await EmitCampSetupAuditAsync(form, current.Id, result.AutoClearedDayOffs);

        SetSuccess(_localizer["VolTrack_Msg_CampSetupSaved"]);
        return RedirectToAction(nameof(Index));
    }

    private async Task EmitCampSetupAuditAsync(
        SetCampSetupForm form, Guid actorUserId,
        IReadOnlyList<int>? autoClearedDayOffs)
    {
        await _auditLogService.LogAsync(
            AuditAction.VolunteerCampSetupSet,
            nameof(VolunteerBuildStatus),
            form.UserId,
            $"BarrioSetupStartDate set to {form.Date}; notes={form.Notes ?? "—"}",
            actorUserId);

        if (autoClearedDayOffs is null || autoClearedDayOffs.Count == 0) return;

        // IAuditLogRepository creates a fresh DbContext per call, so the
        // fan-out can run concurrently without sharing change-tracker state.
        await Task.WhenAll(autoClearedDayOffs.Select(dayOffset =>
            _auditLogService.LogAsync(
                AuditAction.VolunteerDayOffCleared,
                nameof(VolunteerBuildStatus),
                form.UserId,
                $"DayOffset={dayOffset}; auto-cleared by camp-setup change",
                actorUserId)));
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

    [HttpPost("SetDayOff")]
    [Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDayOff(SetDayOffForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(_localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var current = await GetCurrentUserAsync();
        if (current is null) return Forbid();

        var result = await _service.SetDayOffAsync(form.UserId, form.DayOffset, form.Reason, current.Id, ct);
        if (!result.Ok)
        {
            SetError(_localizer[result.ErrorMessageKey ?? "VolTrack_Err_Unknown"]);
            return RedirectToAction(nameof(Index));
        }

        await _auditLogService.LogAsync(
            AuditAction.VolunteerDayOffMarked,
            nameof(VolunteerBuildStatus),
            form.UserId,
            $"DayOffset={form.DayOffset}; reason={(string.IsNullOrWhiteSpace(form.Reason) ? "—" : form.Reason)}",
            current.Id);

        SetSuccess(_localizer["VolTrack_Msg_DayOffMarked"]);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ClearDayOff")]
    [Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearDayOff(ClearDayOffForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(_localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var current = await GetCurrentUserAsync();
        if (current is null) return Forbid();

        var result = await _service.ClearDayOffAsync(form.UserId, form.DayOffset, current.Id, ct);
        if (result.Removed)
        {
            await _auditLogService.LogAsync(
                AuditAction.VolunteerDayOffCleared,
                nameof(VolunteerBuildStatus),
                form.UserId,
                $"DayOffset={form.DayOffset}; cleared by coordinator",
                current.Id);
            SetSuccess(_localizer["VolTrack_Msg_DayOffCleared"]);
        }

        return RedirectToAction(nameof(Index));
    }
}
