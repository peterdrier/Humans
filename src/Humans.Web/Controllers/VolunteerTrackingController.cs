using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime.Text;

namespace Humans.Web.Controllers;

/// <summary>
/// Coordinator tracking heatmap + camp-setup/day-block mutations.
/// Reads gated by ShiftDashboardAccess; writes add VolunteerTrackingWrite.
/// </summary>
[Route("Shifts/Dashboard/VolunteerTracking")]
[Authorize(Policy = PolicyNames.ShiftDashboardAccess)]
public sealed class VolunteerTrackingController(
    IVolunteerTrackingService service,
    IUserService userService,
    IAuditLogService auditLogService,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(
        bool hideNoGaps = false,
        bool hideCampSetup = false,
        bool hideUnbookedSection = false,
        CancellationToken ct = default)
    {
        var data = await service.GetTrackingDataAsync(ct);
        if (!data.HasActiveEvent)
        {
            return View(VolunteerTrackingPageViewModel.Empty);
        }

        // BurnerName lookup for sort key + partial tooltips (cell render uses <vc:human>).
        var displayUserIds = data.MainCohort.Select(r => r.UserId)
            .Concat(data.UnbookedCohort.Select(r => r.UserId))
            .Distinct()
            .ToArray();
        var nameByUserId = new Dictionary<Guid, string>(displayUserIds.Length);
        foreach (var uid in displayUserIds)
        {
            var info = await _userService.GetUserInfoAsync(uid, ct);
            nameByUserId[uid] = info?.BurnerName ?? "";
        }

        var mainSorted = data.MainCohort
            .Where(r => !hideNoGaps || r.GapCount > 0)
            .Where(r => !hideCampSetup || r.BarrioSetupStartDate is null)
            .OrderByDescending(r => r.GapCount)
            .ThenBy(r => r.LastEligibleSignupOffset)
            .ThenBy(r => nameByUserId.GetValueOrDefault(r.UserId, ""), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unbookedSorted = hideUnbookedSection
            ? []
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
            nameByUserId,
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
            SetError(localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var parseResult = LocalDatePattern.Iso.Parse(form.Date);
        if (!parseResult.Success)
        {
            SetError(localizer["VolTrack_Err_BadDate"]);
            return RedirectToAction(nameof(Index));
        }
        var parsed = parseResult.Value;

        var current = await GetCurrentUserInfoAsync();
        if (current is null) return Forbid();

        var result = await service.SetCampSetupAsync(form.UserId, parsed, form.Notes, current.Id, ct);
        if (!result.Ok)
        {
            SetError(localizer[result.ErrorMessageKey ?? "VolTrack_Err_Unknown"]);
            return RedirectToAction(nameof(Index));
        }

        await EmitCampSetupAuditAsync(form, current.Id, result.AutoClearedDayOffs);

        SetSuccess(localizer["VolTrack_Msg_CampSetupSaved"]);
        return RedirectToAction(nameof(Index));
    }

    private async Task EmitCampSetupAuditAsync(
        SetCampSetupForm form, Guid actorUserId,
        IReadOnlyList<int>? autoClearedDayOffs)
    {
        await auditLogService.LogAsync(
            AuditAction.VolunteerCampSetupSet,
            nameof(VolunteerBuildStatus),
            form.UserId,
            $"BarrioSetupStartDate set to {form.Date}; notes={form.Notes ?? "—"}",
            actorUserId);

        if (autoClearedDayOffs is null || autoClearedDayOffs.Count == 0) return;

        // Fresh DbContext per call → safe to fan out concurrently.
        await Task.WhenAll(autoClearedDayOffs.Select(dayOffset =>
            auditLogService.LogAsync(
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
            SetError(localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var current = await GetCurrentUserInfoAsync();
        if (current is null) return Forbid();

        await service.ClearCampSetupAsync(userId, current.Id, ct);

        await auditLogService.LogAsync(
            AuditAction.VolunteerCampSetupCleared,
            nameof(VolunteerBuildStatus),
            userId,
            "BarrioSetupStartDate cleared",
            current.Id);

        SetSuccess(localizer["VolTrack_Msg_CampSetupCleared"]);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SetDayOff")]
    [Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDayOff(SetDayOffForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var current = await GetCurrentUserInfoAsync();
        if (current is null) return Forbid();

        var result = await service.SetDayOffAsync(form.UserId, form.DayOffset, form.Reason, current.Id, ct);
        if (!result.Ok)
        {
            SetError(localizer[result.ErrorMessageKey ?? "VolTrack_Err_Unknown"]);
            return RedirectToAction(nameof(Index));
        }

        await auditLogService.LogAsync(
            AuditAction.VolunteerDayOffMarked,
            nameof(VolunteerBuildStatus),
            form.UserId,
            $"DayOffset={form.DayOffset}; reason={(string.IsNullOrWhiteSpace(form.Reason) ? "—" : form.Reason)}",
            current.Id);

        SetSuccess(localizer["VolTrack_Msg_DayOffMarked"]);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("ClearDayOff")]
    [Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearDayOff(ClearDayOffForm form, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(localizer["VolTrack_Err_BadRequest"]);
            return RedirectToAction(nameof(Index));
        }

        var current = await GetCurrentUserInfoAsync();
        if (current is null) return Forbid();

        var result = await service.ClearDayOffAsync(form.UserId, form.DayOffset, current.Id, ct);
        if (result.Removed)
        {
            await auditLogService.LogAsync(
                AuditAction.VolunteerDayOffCleared,
                nameof(VolunteerBuildStatus),
                form.UserId,
                $"DayOffset={form.DayOffset}; cleared by coordinator",
                current.Id);
            SetSuccess(localizer["VolTrack_Msg_DayOffCleared"]);
        }

        return RedirectToAction(nameof(Index));
    }
}
