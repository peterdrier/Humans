using System.Globalization;
using System.Text.Json;
using Humans.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Shifts")]
public class ShiftsController : HumansControllerBase
{
    private readonly IShiftManagementService _shiftMgmt;
    private readonly IShiftSignupService _signupService;
    private readonly IGeneralAvailabilityService _availabilityService;
    private readonly ITeamService _teamService;
    private readonly IAuditLogService _auditLogService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly IClock _clock;
    private readonly ShiftBrowsePageBuilder _browsePageBuilder;
    private readonly ILogger<ShiftsController> _logger;

    public ShiftsController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        ITeamService teamService,
        IAuditLogService auditLogService,
        IStringLocalizer<SharedResource> localizer,
        UserManager<User> userManager,
        IClock clock,
        ShiftBrowsePageBuilder browsePageBuilder,
        ILogger<ShiftsController> logger)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _teamService = teamService;
        _auditLogService = auditLogService;
        _localizer = localizer;
        _clock = clock;
        _browsePageBuilder = browsePageBuilder;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? departmentId, string? fromDate, string? toDate, string? period, bool showFull = false, [FromQuery(Name = "tags")] List<Guid>? tagIds = null, string? sort = null, [FromQuery(Name = "periods")] List<string>? periods = null)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var es = await _shiftMgmt.GetActiveAsync();
        if (es is null) return View("NoActiveEvent");

        var isPrivileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                           (await _shiftMgmt.GetCoordinatorTeamIdsAsync(user.Id)).Count > 0;

        var userSignups = await _signupService.GetByUserAsync(user.Id, es.Id);
        var hasSignups = userSignups.Count > 0;
        var userActiveSignupsForUi = await LoadUserActiveSignupsForUiAsync(user.Id);

        if (!CanBrowseShifts(es, isPrivileged, hasSignups))
            return View("BrowsingClosed");

        var model = await _browsePageBuilder.BuildAsync(new ShiftBrowsePageRequest(
            es,
            user.Id,
            userSignups,
            userActiveSignupsForUi,
            departmentId,
            fromDate,
            toDate,
            period,
            showFull,
            tagIds,
            sort,
            periods,
            isPrivileged));

        return View(model);
    }

    private static bool CanBrowseShifts(EventSettings eventSettings, bool isPrivileged, bool hasSignups) =>
        eventSettings.IsShiftBrowsingOpen || isPrivileged || hasSignups;

    [HttpPost("SignUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId, Guid? departmentId, string? fromDate, string? toDate, string? period, [FromForm(Name = "tags")] List<Guid>? tagIds, [FromForm(Name = "periods")] List<string>? periods = null, string? sort = null)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
        var result = await _signupService.SignUpAsync(user.Id, shiftId, isPrivileged: privileged);

        return RedirectToBrowseWithSignupResult(
            result,
            successMessage: "Signed up successfully!",
            warningPrefix: "Signed up successfully.",
            errorFallback: "Shift signup failed.",
            departmentId,
            fromDate,
            toDate,
            period,
            tagIds,
            periods,
            sort);
    }

    [HttpPost("SignUpRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUpRange(Guid rotaId, int startDayOffset, int endDayOffset, Guid? departmentId, string? fromDate, string? toDate, string? period, [FromForm(Name = "tags")] List<Guid>? tagIds, [FromForm(Name = "periods")] List<string>? periods = null, string? sort = null)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
        var result = await _signupService.SignUpRangeAsync(user.Id, rotaId, startDayOffset, endDayOffset, isPrivileged: privileged, skipConflicts: true);

        return RedirectToBrowseWithSignupResult(
            result,
            successMessage: "Signed up for date range!",
            warningPrefix: "Signed up for date range.",
            errorFallback: "Shift range signup failed.",
            departmentId,
            fromDate,
            toDate,
            period,
            tagIds,
            periods,
            sort);
    }

    private IActionResult RedirectToBrowseWithSignupResult(
        SignupResult result,
        string successMessage,
        string warningPrefix,
        string errorFallback,
        Guid? departmentId,
        string? fromDate,
        string? toDate,
        string? period,
        List<Guid>? tagIds,
        List<string>? periods,
        string? sort)
    {
        if (!result.Success)
            SetError(result.Error ?? errorFallback);
        else
            SetSuccess(result.Warning is not null ? $"{warningPrefix} Note: {result.Warning}" : successMessage);

        return RedirectToAction(nameof(Index), BuildFilterRouteValues(departmentId, fromDate, toDate, period, tagIds, sort: sort, periods: periods));
    }

    private static RouteValueDictionary BuildFilterRouteValues(Guid? departmentId, string? fromDate, string? toDate, string? period, List<Guid>? tagIds, string? sort = null, List<string>? periods = null)
    {
        var rv = new RouteValueDictionary();
        if (departmentId.HasValue) rv["departmentId"] = departmentId.Value;
        if (!string.IsNullOrEmpty(fromDate)) rv["fromDate"] = fromDate;
        if (!string.IsNullOrEmpty(toDate)) rv["toDate"] = toDate;
        if (!string.IsNullOrEmpty(period)) rv["period"] = period;
        if (!string.IsNullOrEmpty(sort)) rv["sort"] = sort;
        if (tagIds is { Count: > 0 })
            for (var i = 0; i < tagIds.Count; i++)
                rv[$"tags[{i}]"] = tagIds[i];
        if (periods is { Count: > 0 })
            for (var i = 0; i < periods.Count; i++)
                rv[$"periods[{i}]"] = periods[i];
        return rv;
    }

    [HttpPost("BailRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BailRange(Guid signupBlockId)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        try
        {
            await _signupService.BailRangeAsync(signupBlockId, user.Id);
            SetSuccess("Successfully bailed from shift range.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to bail shift range {SignupBlockId} for user {UserId}", signupBlockId, user.Id);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Mine));
    }

    [HttpPost("Bail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Bail(Guid signupId, string? reason)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var result = await _signupService.BailAsync(signupId, user.Id, reason);

        if (!result.Success)
        {
            SetError(result.Error ?? "Shift bail failed.");
            return RedirectToAction(nameof(Mine));
        }

        SetSuccess("Successfully bailed from shift.");
        return RedirectToAction(nameof(Mine));
    }

    [HttpGet("Mine")]
    public async Task<IActionResult> Mine()
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var es = await _shiftMgmt.GetActiveAsync();

        var signups = es is not null
            ? await _signupService.GetByUserAsync(user.Id, es.Id)
            : [];

        var now = _clock.GetCurrentInstant();
        var model = new MyShiftsViewModel { EventSettings = es };

        var mineTeamNames = await LoadTeamNamesForSignupsAsync(signups);
        var buckets = ShiftSignupBucketer.Build(signups, es, mineTeamNames, now, onMissingSignupData: signup =>
            _logger.LogWarning(
                "Skipping shift signup {SignupId} for user {UserId} because related shift data was missing",
                signup.Id,
                user.Id));

        model.Upcoming = buckets.Upcoming;
        model.Pending = buckets.Pending;
        model.Past = buckets.Past;

        await PopulateAvailabilityAsync(model, user.Id, es);
        await EnsureICalUrlAsync(model, user);

        return View(model);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadTeamNamesForSignupsAsync(IReadOnlyList<ShiftSignup> signups)
    {
        var teamIds = ShiftSignupBucketer.GetTeamIds(signups);
        return teamIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _teamService.GetTeamNamesByIdsAsync(teamIds);
    }

    private async Task PopulateAvailabilityAsync(MyShiftsViewModel model, Guid userId, EventSettings? eventSettings)
    {
        if (eventSettings is null) return;

        var availability = await _availabilityService.GetByUserAsync(userId, eventSettings.Id);
        if (availability is not null)
            model.AvailableDayOffsets = availability.AvailableDayOffsets.ToList();
    }

    private async Task EnsureICalUrlAsync(MyShiftsViewModel model, User user)
    {
        if (user.ICalToken is null)
        {
            user.ICalToken = Guid.NewGuid();
            await UpdateCurrentUserAsync(user);
        }

        model.ICalUrl = $"{Request.Scheme}://{Request.Host}/ICal/{user.ICalToken}.ics";
    }

    [HttpPost("Mine/Availability")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAvailability(List<int>? dayOffsets)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var es = await _shiftMgmt.GetActiveAsync();
        if (es is null) return BadRequest("No active event.");

        await _availabilityService.SetAvailabilityAsync(user.Id, es.Id, dayOffsets ?? []);
        SetSuccess("Availability updated.");
        return RedirectToAction(nameof(Mine));
    }

    [HttpPost("Mine/RegenerateIcal")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegenerateIcal()
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        user.ICalToken = Guid.NewGuid();
        await UpdateCurrentUserAsync(user);

        SetSuccess("iCal URL regenerated.");
        return RedirectToAction(nameof(Mine));
    }

    [HttpPost("Preferences/Tags")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTagPreferences([FromForm(Name = "tagIds")] List<Guid>? tagIds)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        await _shiftMgmt.SetVolunteerTagPreferencesAsync(user.Id, tagIds ?? []);
        SetSuccess("Tag preferences saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Settings")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Settings()
    {
        var es = await _shiftMgmt.GetActiveAsync();
        return View(es is null ? new EventSettingsViewModel() : MapEventSettingsToViewModel(es));
    }

    private static EventSettingsViewModel MapEventSettingsToViewModel(EventSettings es) => new()
    {
        Id = es.Id,
        EventName = es.EventName,
        TimeZoneId = es.TimeZoneId,
        GateOpeningDate = LocalDatePattern.Iso.Format(es.GateOpeningDate),
        BuildStartOffset = es.BuildStartOffset,
        EventEndOffset = es.EventEndOffset,
        StrikeEndOffset = es.StrikeEndOffset,
        FirstCrewStartOffset = es.FirstCrewStartOffset,
        SetupWeekStartOffset = es.SetupWeekStartOffset,
        PreEventWeekStartOffset = es.PreEventWeekStartOffset,
        FinishingWeekendStartOffset = es.FinishingWeekendStartOffset,
        EarlyEntryCapacityJson = JsonSerializer.Serialize(es.EarlyEntryCapacity),
        BarriosEarlyEntryAllocationJson = es.BarriosEarlyEntryAllocation is not null
            ? JsonSerializer.Serialize(es.BarriosEarlyEntryAllocation)
            : null,
        EarlyEntryClose = es.EarlyEntryClose.HasValue
            ? InstantPattern.General.Format(es.EarlyEntryClose.Value)
            : null,
        IsShiftBrowsingOpen = es.IsShiftBrowsingOpen,
        GlobalVolunteerCap = es.GlobalVolunteerCap,
        ReminderLeadTimeHours = es.ReminderLeadTimeHours,
        IsActive = es.IsActive,
    };

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Settings(EventSettingsViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var parsed = EventSettingsFormMapper.Parse(model);
        if (!parsed.Success)
            return ViewWithFormErrors(model, parsed.Errors);

        var draft = parsed.Draft!;

        if (model.Id.HasValue)
        {
            var existing = await _shiftMgmt.GetByIdAsync(model.Id.Value);
            if (existing is null) return NotFound();

            EventSettingsFormMapper.Apply(existing, draft);
            await _shiftMgmt.UpdateAsync(existing);
        }
        else
        {
            await _shiftMgmt.CreateAsync(EventSettingsFormMapper.Create(draft, _clock.GetCurrentInstant()));
        }

        SetSuccess("Event settings saved.");
        return RedirectToAction(nameof(Settings));
    }

    private IActionResult ViewWithFormErrors(EventSettingsViewModel model, IReadOnlyList<EventSettingsFormError> errors)
    {
        foreach (var error in errors)
            ModelState.AddModelError(error.FieldName, error.Message);

        return View(model);
    }

    private async Task<IReadOnlyList<UserSignupConflictItem>> LoadUserActiveSignupsForUiAsync(Guid userId)
    {
        var allActiveSignups = await _signupService.GetActiveSignupsForUserAsync(userId);
        return allActiveSignups
            .Where(s => s.Shift?.Rota?.EventSettings is not null)
            .Select(s =>
            {
                var sEs = s.Shift!.Rota!.EventSettings!;
                var absStart = s.Shift.GetAbsoluteStart(sEs);
                var absEnd = s.Shift.GetAbsoluteEnd(sEs);
                var tz = DateTimeZoneProviders.Tzdb[sEs.TimeZoneId];
                var localStart = absStart.InZone(tz).LocalDateTime;
                var localEnd = absEnd.InZone(tz).LocalDateTime;
                return new UserSignupConflictItem(
                    Date: localStart.Date,
                    RotaName: s.Shift.Rota.Name,
                    AbsoluteStart: absStart,
                    AbsoluteEnd: absEnd,
                    DisplayStart: localStart.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
                    DisplayEnd: localEnd.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture));
            })
            .ToList();
    }

    private static (LocalDate From, LocalDate To) GetPeriodDateRange(EventSettings es, ShiftPeriod period)
    {
        return period switch
        {
            ShiftPeriod.Build => (
                es.GateOpeningDate.PlusDays(es.BuildStartOffset),
                es.GateOpeningDate.PlusDays(-1)),
            ShiftPeriod.Event => (
                es.GateOpeningDate,
                es.GateOpeningDate.PlusDays(es.EventEndOffset)),
            ShiftPeriod.Strike => (
                es.GateOpeningDate.PlusDays(es.EventEndOffset + 1),
                es.GateOpeningDate.PlusDays(es.StrikeEndOffset)),
            _ => (
                es.GateOpeningDate.PlusDays(es.BuildStartOffset),
                es.GateOpeningDate.PlusDays(es.StrikeEndOffset))
        };
    }

    // ==========================================================================
    // Orphan-signup reconciliation (admin diagnostic)
    // ==========================================================================
    //
    // Surfaces ShiftSignups whose Id has no audit row tying the signup to a
    // creation-or-confirmation moment (ShiftSignupCreated, ShiftSignupVoluntold,
    // or ShiftSignupConfirmed). These are the rows behind the "user bailed
    // from a shift they never signed up for" support thread.
    //
    // ShiftSignupConfirmed is included so legacy data isn't falsely flagged:
    // pre-change, auto-confirmed self-signups wrote ShiftSignupConfirmed at
    // creation time, and Pending → Confirmed transitions also write it. In
    // both cases the human has a verifiable trail, even if the original
    // Pending creation moment was never audited (the bug we're hunting). A
    // true orphan is a signup with NONE of {Created, Voluntold, Confirmed}
    // — i.e. a legacy Pending self-signup that went straight to
    // Bailed/Refused/Cancelled without ever passing through Confirm.

    [HttpGet("OrphanSignups")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> OrphanSignups(
        [FromServices] IUserService userService,
        CancellationToken ct)
    {
        var allSignups = await _signupService.GetAllForOrphanScanAsync(ct);
        var auditedIds = await _auditLogService.GetEntityIdsForEntityTypeActionsAsync(
            nameof(ShiftSignup),
            [AuditAction.ShiftSignupCreated, AuditAction.ShiftSignupVoluntold, AuditAction.ShiftSignupConfirmed],
            ct);

        var orphans = allSignups.Where(s => !auditedIds.Contains(s.Id)).ToList();
        var users = await ResolveOrphanActorsAsync(orphans, userService, ct);
        var rows = BuildOrphanRows(orphans, users);

        return View(new OrphanSignupsViewModel(
            TotalSignups: allSignups.Count,
            OrphanCount: rows.Count,
            UniqueUsers: rows.Select(r => r.UserId).Distinct().Count(),
            Rows: rows));
    }

    private static async Task<IReadOnlyDictionary<Guid, UserInfo>> ResolveOrphanActorsAsync(
        IReadOnlyList<OrphanSignupSnapshot> orphans, IUserService userService, CancellationToken ct)
    {
        // Display-name resolution goes through the Users section directly —
        // OrphanSignups is a §2c cross-section consumer of audit-log
        // entity-ids, not a render-the-audit-log view, so the names belong
        // to IUserService rather than IAuditViewerService.
        var userIds = orphans
            .SelectMany(s => new[] { s.UserId, s.ReviewedByUserId, s.EnrolledByUserId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        return userIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(userIds, ct);
    }

    private static List<OrphanSignupRow> BuildOrphanRows(
        IReadOnlyList<OrphanSignupSnapshot> orphans,
        IReadOnlyDictionary<Guid, UserInfo> users)
    {
        string? GetName(Guid? id) => id.HasValue && users.TryGetValue(id.Value, out var u) ? u.DisplayName : null;

        return orphans
            .Select(s => new OrphanSignupRow(
                SignupId: s.Id,
                UserId: s.UserId,
                UserDisplayName: GetName(s.UserId) ?? s.UserId.ToString(),
                RotaName: s.RotaName,
                ShiftDate: s.ShiftDate,
                Status: s.Status,
                CreatedAt: s.CreatedAt,
                ReviewedByUserId: s.ReviewedByUserId,
                ReviewedByDisplayName: GetName(s.ReviewedByUserId),
                EnrolledByUserId: s.EnrolledByUserId,
                EnrolledByDisplayName: GetName(s.EnrolledByUserId),
                SignupBlockId: s.SignupBlockId))
            .OrderBy(r => r.UserDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.CreatedAt)
            .ToList();
    }
}

public record OrphanSignupRow(
    Guid SignupId,
    Guid UserId,
    string UserDisplayName,
    string RotaName,
    LocalDate ShiftDate,
    SignupStatus Status,
    Instant CreatedAt,
    Guid? ReviewedByUserId,
    string? ReviewedByDisplayName,
    Guid? EnrolledByUserId,
    string? EnrolledByDisplayName,
    Guid? SignupBlockId);

public record OrphanSignupsViewModel(
    int TotalSignups,
    int OrphanCount,
    int UniqueUsers,
    IReadOnlyList<OrphanSignupRow> Rows);
