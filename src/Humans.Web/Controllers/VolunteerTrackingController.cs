using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Humans.Web.Models.VolunteerTracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
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
    IShiftManagementService shiftManagementService,
    IVolunteerTrackingExportService exportService,
    VolunteerTrackingXlsxBuilder xlsxBuilder,
    IUserServiceRead userService,
    IAuditLogService auditLogService,
    IStringLocalizer<SharedResource> localizer) : HumansControllerBase(userService)
{
    private readonly IUserServiceRead _userService = userService;

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

        // Export card form state — driven by the active event's departments.
        // GetActiveAsync is the same lookup the Index page is gated on (we
        // already know HasActiveEvent is true here), so the result is non-null
        // in practice; defensive fall-back keeps the page renderable if a race
        // empties EventSettings between the two calls.
        var activeEvent = await shiftManagementService.GetActiveAsync();
        var departments = activeEvent is null
            ? []
            : await shiftManagementService.GetDepartmentsWithRotasAsync(activeEvent.Id);

        var model = new VolunteerTrackingPageViewModel(
            data.BuildStartOffset,
            data.GateOpeningDate,
            data.Today,
            mainSorted,
            unbookedSorted,
            nameByUserId,
            hideNoGaps,
            hideCampSetup,
            hideUnbookedSection)
        {
            ExportForm = new VolunteerTrackingExportFormViewModel
            {
                Departments = departments,
                SelectedDepartmentId = null,
                SelectedPeriod = ShiftPeriod.Event,
                StartDate = null,
                EndDate = null,
            },
        };

        return View(model);
    }

    [HttpGet("ExportXlsx")]
    public async Task<IActionResult> ExportXlsx(
        Guid? departmentId,
        string? startDate,
        string? endDate,
        ShiftPeriod? period,
        BuildSubPeriod? subPeriod = null,
        CancellationToken ct = default)
    {
        var eventSettings = await shiftManagementService.GetActiveAsync();
        if (eventSettings is null)
        {
            SetError(localizer["VolTrack_NoActiveEvent"]);
            return RedirectToAction(nameof(Index));
        }

        var parsedStart = TryParseLocalDate(startDate);
        var parsedEnd = TryParseLocalDate(endDate);
        var (activeStart, activeEnd) = ShiftFilterResolver.Resolve(period, parsedStart, parsedEnd);

        // Resolve range:
        //   period=Build + subPeriod set → narrow to that sub-period's day-offset window
        //   period set → period's window (Build/Event/Strike each have distinct windows)
        //   period null + explicit dates → those dates
        //   period null + no dates → whole event (Build → Strike inclusive)
        LocalDate rangeStart, rangeEnd;
        if (period == ShiftPeriod.Build && subPeriod.HasValue)
        {
            var (startOffset, endExclusive) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, eventSettings);
            rangeStart = eventSettings.GateOpeningDate.PlusDays(startOffset);
            rangeEnd = eventSettings.GateOpeningDate.PlusDays(endExclusive - 1);
        }
        else if (period.HasValue)
        {
            (rangeStart, rangeEnd) = ShiftFilterResolver.ResolvePeriodRange(period.Value, eventSettings);
        }
        else if (activeStart.HasValue && activeEnd.HasValue)
        {
            (rangeStart, rangeEnd) = (activeStart.Value, activeEnd.Value);
        }
        else
        {
            rangeStart = eventSettings.GateOpeningDate.PlusDays(eventSettings.BuildStartOffset);
            rangeEnd = eventSettings.GateOpeningDate.PlusDays(eventSettings.StrikeEndOffset);
        }
        // Guard against a hand-crafted URL with endDate before startDate (the form's HTML5
        // validation would block this, but the action is reachable directly).
        if (rangeEnd < rangeStart)
        {
            (rangeStart, rangeEnd) = (rangeEnd, rangeStart);
        }

        var actorInfo = await GetCurrentUserInfoAsync(ct);
        var actor = actorInfo?.BurnerName ?? localizer["VolTrack_UnknownUser"].Value;

        var request = new VolunteerExportRequest(
            EventSettingsId: eventSettings.Id,
            DepartmentId: departmentId,
            StartDate: rangeStart,
            EndDate: rangeEnd,
            Period: period,
            ActorPlayaName: actor,
            GeneratedAtUtc: SystemClock.Instance.GetCurrentInstant());

        var model = await exportService.BuildAsync(request, ct);
        var result = xlsxBuilder.Build(model);
        return File(result.Content, result.ContentType, result.FileName);
    }

    private static LocalDate? TryParseLocalDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var parsed = LocalDatePattern.Iso.Parse(input);
        return parsed.Success ? parsed.Value : null;
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
