using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Base.Controllers;
using Humans.Base.Authorization;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Text;
using Humans.Users.Contracts;

namespace Humans.Shifts.Controllers;

/// <summary>Reads need ShiftsPolicies.Read; the export needs Manage and writes need ManageVolunteers.</summary>
[Route("Shifts/Dashboard/VolunteerTracking")]
[Authorize(Policy = ShiftsPolicies.Read)]
internal sealed class VolunteerTrackingController(
    IVolunteerTrackingManagement service,
    IShiftManagement shiftManagementService,
    IBurnSettingsService burnSettings,
    IVolunteerTrackingExportService exportService,
    VolunteerTrackingXlsxBuilder xlsxBuilder,
    IUserService userService,
    IStringLocalizer<ShiftsResource> localizer,
    ILogger<VolunteerTrackingController> logger) : HumansControllerBase(userService)
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

        // The sort key and tooltips; cells render names through <vc:human>.
        var displayUserIds = data.MainCohort.Select(r => r.UserId)
            .Concat(data.UnbookedCohort.Select(r => r.UserId))
            .Distinct()
            .ToArray();
        IReadOnlyDictionary<Guid, UserInfo> infos = displayUserIds.Length == 0
            ? new Dictionary<Guid, UserInfo>()
            : await _userService.GetUserInfosAsync(displayUserIds, ct);
        var nameByUserId = displayUserIds.ToDictionary(uid => uid, uid => infos.GetValueOrDefault(uid)?.BurnerName ?? "");

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

        // The page is gated on an active event; the null fallback covers a race between the two reads.
        var activeEvent = await burnSettings.GetActiveAsync(ct);
        var departments = activeEvent is null
            ? []
            : await shiftManagementService.GetDepartmentsWithRotasAsync(activeEvent.Id, ct);

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
                Departments = departments.OrderBy(d => d.TeamName, StringComparer.Ordinal).ToList(),
                SelectedDepartmentId = null,
                SelectedPeriod = ShiftPeriod.Event,
                StartDate = null,
                EndDate = null,
            },
        };

        return View(model);
    }

    [HttpGet("ExportXlsx")]
    [Authorize(Policy = ShiftsPolicies.Manage)]
    public async Task<IActionResult> ExportXlsx(
        Guid? departmentId,
        string? startDate,
        string? endDate,
        ShiftPeriod? period,
        BuildSubPeriod? subPeriod = null,
        CancellationToken ct = default)
    {
        var eventSettings = await burnSettings.GetActiveAsync(ct);
        if (eventSettings is null)
        {
            SetError(localizer["Shifts_VolTrack_NoActiveEvent"]);
            return RedirectToAction(nameof(Index));
        }

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed volunteer tracking export query");
            return BadRequest(ModelState);
        }
        var range = ShiftFilterResolver.ResolveExportRange(eventSettings, startDate, endDate, period, subPeriod);
        if (range is null)
        {
            logger.LogWarning("Rejected volunteer tracking export date range {StartDate} through {EndDate}, period {Period}",
                startDate, endDate, period);
            return BadRequest("Enter a complete, valid export date range within the supported calendar.");
        }
        var (rangeStart, rangeEnd) = range.Value;

        var actorInfo = await GetCurrentUserInfoAsync(ct);
        if (actorInfo is null) return Unauthorized();
        var actor = actorInfo.BurnerName;

        var request = new VolunteerExportRequest(
            EventSettingsId: eventSettings.Id,
            DepartmentId: departmentId,
            StartDate: rangeStart,
            EndDate: rangeEnd,
            Period: period,
            ActorUserId: actorInfo.Id,
            ActorPlayaName: actor,
            GeneratedAtUtc: SystemClock.Instance.GetCurrentInstant());

        var model = await exportService.BuildAsync(request, ct);
        var result = xlsxBuilder.Build(model);
        return File(result.Content, result.ContentType, result.FileName);
    }

    [HttpPost("SetCampSetup")]
    [Authorize(Policy = ShiftsPolicies.ManageVolunteers)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCampSetup(SetCampSetupForm form, string? returnUrl, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(localizer["Shifts_VolTrack_Err_BadRequest"]);
            return RedirectBack(returnUrl);
        }

        var parseResult = LocalDatePattern.Iso.Parse(form.Date);
        if (!parseResult.Success)
        {
            SetError(localizer["Shifts_VolTrack_Err_BadDate"]);
            return RedirectBack(returnUrl);
        }
        var parsed = parseResult.Value;

        var current = await GetCurrentUserInfoAsync(ct);
        if (current is null) return Forbid();

        if (form.UserId == Guid.Empty)
        {
            logger.LogWarning("Rejected camp setup form without a member ID from coordinator {CoordinatorId}", current.Id);
            return BadRequest();
        }

        var result = await service.SetCampSetupAsync(form.UserId, parsed, form.Notes, current.Id, ct);
        if (!result.Ok)
        {
            SetError(localizer[result.ErrorMessageKey ?? "Shifts_VolTrack_Err_Unknown"]);
            return RedirectBack(returnUrl);
        }

        SetSuccess(localizer["Shifts_VolTrack_Msg_CampSetupSaved"]);
        return RedirectBack(returnUrl);
    }

    [HttpPost("ClearCampSetup")]
    [Authorize(Policy = ShiftsPolicies.ManageVolunteers)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearCampSetup(Guid userId, string? returnUrl, CancellationToken ct)
    {
        if (userId == Guid.Empty)
        {
            SetError(localizer["Shifts_VolTrack_Err_BadRequest"]);
            return RedirectBack(returnUrl);
        }

        var current = await GetCurrentUserInfoAsync(ct);
        if (current is null) return Forbid();

        await service.ClearCampSetupAsync(userId, current.Id, ct);

        SetSuccess(localizer["Shifts_VolTrack_Msg_CampSetupCleared"]);
        return RedirectBack(returnUrl);
    }

    [HttpPost("SetDayOff")]
    [Authorize(Policy = ShiftsPolicies.ManageVolunteers)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDayOff(SetDayOffForm form, string? returnUrl, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(localizer["Shifts_VolTrack_Err_BadRequest"]);
            return RedirectBack(returnUrl);
        }

        var current = await GetCurrentUserInfoAsync(ct);
        if (current is null) return Forbid();

        if (form.UserId == Guid.Empty)
        {
            logger.LogWarning("Rejected day off form without a member ID from coordinator {CoordinatorId}", current.Id);
            return BadRequest();
        }

        var result = await service.SetDayOffAsync(
            form.UserId,
            form.DayOffset,
            form.Reason,
            current.Id,
            ct);
        if (!result.Ok)
        {
            SetError(localizer[result.ErrorMessageKey ?? "Shifts_VolTrack_Err_Unknown"]);
            return RedirectBack(returnUrl);
        }

        SetSuccess(localizer["Shifts_VolTrack_Msg_DayOffMarked"]);
        return RedirectBack(returnUrl);
    }

    [HttpPost("ClearDayOff")]
    [Authorize(Policy = ShiftsPolicies.ManageVolunteers)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearDayOff(ClearDayOffForm form, string? returnUrl, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            SetError(localizer["Shifts_VolTrack_Err_BadRequest"]);
            return RedirectBack(returnUrl);
        }

        var current = await GetCurrentUserInfoAsync(ct);
        if (current is null) return Forbid();

        var result = await service.ClearDayOffAsync(
            form.UserId,
            form.DayOffset,
            current.Id,
            ct);
        if (result.Removed)
        {
            SetSuccess(localizer["Shifts_VolTrack_Msg_DayOffCleared"]);
        }
        else
        {
            SetInfo(localizer["Shifts_VolTrack_Msg_DayOffNotSet"]);
        }

        return RedirectBack(returnUrl);
    }

    [HttpPost("SetAvailabilityDay")]
    [Authorize(Policy = ShiftsPolicies.ManageVolunteers)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SetAvailabilityDay(
        Guid? userId, int? dayOffset, string? returnUrl, CancellationToken ct)
        => UpdateAvailabilityDayAsync(userId, dayOffset, returnUrl, available: true, ct);

    [HttpPost("ClearAvailabilityDay")]
    [Authorize(Policy = ShiftsPolicies.ManageVolunteers)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ClearAvailabilityDay(
        Guid? userId, int? dayOffset, string? returnUrl, CancellationToken ct)
        => UpdateAvailabilityDayAsync(userId, dayOffset, returnUrl, available: false, ct);

    private async Task<IActionResult> UpdateAvailabilityDayAsync(
        Guid? userId, int? dayOffset, string? returnUrl, bool available, CancellationToken ct)
    {
        var es = await burnSettings.GetActiveAsync(ct);
        if (es is null) { SetError(localizer["Shifts_VolTrack_Err_BadRequest"]); return RedirectBack(returnUrl); }

        var current = await GetCurrentUserInfoAsync(ct);
        if (current is null) return Forbid();

        if (!ModelState.IsValid || userId is not { } targetUserId || dayOffset is not { } offset
            || targetUserId == Guid.Empty)
        {
            logger.LogWarning("Rejected malformed coordinator availability {Operation} for user {UserId}, day {DayOffset}",
                available ? "addition" : "removal", userId, dayOffset);
            return BadRequest();
        }

        var changed = await service.SetDayAvailabilityAsync(
            targetUserId,
            es.Id,
            offset,
            available,
            current.Id,
            ct);
        if (changed)
        {
            SetSuccess(localizer[available ? "Shifts_VolTrack_Msg_AvailabilitySet" : "Shifts_VolTrack_Msg_AvailabilityCleared"]);
        }
        return RedirectBack(returnUrl);
    }

    private IActionResult RedirectBack(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
}
