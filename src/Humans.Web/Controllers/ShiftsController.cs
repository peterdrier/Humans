using System.Globalization;
using System.Text.Json;
using Humans.Application;
using Humans.Application.Extensions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Humans.Web.Models.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Shifts")]
public class ShiftsController(
    IShiftManagementService shiftMgmt,
    IShiftSignupService signupService,
    IVolunteerTrackingService volunteerTrackingService,
    IShiftView shiftView,
    ITeamServiceRead teamService,
    IAuditLogService auditLogService,
    IUserService userService,
    IStringLocalizer<SharedResource> localizer,
    IClock clock,
    ShiftBrowsePageBuilder browsePageBuilder,
    ILogger<ShiftsController> logger) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? departmentId, string? fromDate, string? toDate, string? period, bool showFull = false, [FromQuery(Name = "tags")] List<Guid>? tagIds = null, string? sort = null, [FromQuery(Name = "periods")] List<string>? periods = null)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        if (RedirectIfNameMissing(user) is { } nameGate) return nameGate;

        var es = await shiftMgmt.GetActiveAsync();
        if (es is null) return View("NoActiveEvent");

        var isPrivileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                           (await shiftMgmt.GetCoordinatorTeamIdsAsync(user.Id)).Count > 0;

        // see #720: cached ShiftUserView, already event-scoped.
        var userView = await shiftView.GetUserAsync(user.Id);
        var userSignups = userView.Signups;
        var hasSignups = userSignups.Count > 0;
        var userActiveSignupsForUi = await LoadUserActiveSignupsForUiAsync(user.Id);

        if (!CanBrowseShifts(es, isPrivileged, hasSignups))
            return View("BrowsingClosed");

        var model = await browsePageBuilder.BuildAsync(new ShiftBrowsePageRequest(
            es,
            user.Id,
            userSignups,
            userView.TagPreferences,
            userActiveSignupsForUi,
            departmentId,
            fromDate,
            toDate,
            period,
            showFull,
            tagIds,
            sort,
            periods,
            isPrivileged),
            HttpContext.RequestAborted);

        // Dietary-prompt tightening (#279): lock the rota Sign-Up buttons + show the banner
        // when this human has a qualifying signup but no dietary preference on file.
        model.UserId = user.Id;
        model.SignupsBlockedByMissingDietary = await ComputeSignupsBlockedByMissingDietaryAsync(user, HttpContext.RequestAborted);

        return View(model);
    }

    private static bool CanBrowseShifts(EventSettings eventSettings, bool isPrivileged, bool hasSignups) =>
        eventSettings.IsShiftBrowsingOpen || isPrivileged || hasSignups;

    // No legal name → bounce to onboarding widget (Names step); signup needs it for the rota report.
    private IActionResult? RedirectIfNameMissing(UserInfo user)
    {
        if (user.HasRequiredNameFields) return null;
        SetInfo(localizer["Onboarding_NameRequiredBeforeShifts"].Value);
        return RedirectToAction(nameof(OnboardingWidgetController.Index), "OnboardingWidget");
    }

    // AJAX per-day toggle: signs up or bails a single shift for the current user
    // and returns the re-rendered row partial. Signup/bail outcome and the new
    // active-signup count travel back as response headers (X-Signed-Up,
    // X-My-Signup-Count, X-Toast-*); the name/dietary gates short-circuit to a
    // 204 with X-Redirect so the client navigates instead of swapping a row.
    [HttpPost("ToggleDay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleDay(Guid shiftId, CancellationToken ct)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null) return currentUserNotFound;

        if (RedirectIfNameMissing(user) is not null)
            return RedirectHeader(Url.Action(
                nameof(OnboardingWidgetController.Index), "OnboardingWidget"));

        var es = await shiftMgmt.GetActiveAsync()
            ?? throw new InvalidOperationException("ToggleDay requires an active event.");

        // Resolve sign-up-vs-bail before any gate: the dietary requirement applies to
        // signing up, never to removing a signup you already hold. Event-scoped read so
        // the count below matches the page badge (which is per active event).
        var signups = await signupService.GetByUserAsync(user.Id, es.Id);
        var existing = signups.FirstOrDefault(s =>
            s.ShiftId == shiftId && s.Status is SignupStatus.Confirmed or SignupStatus.Pending);

        if (existing is null && await ShiftNeedsDietaryFirstAsync(user, shiftId))
        {
            SetInfo(localizer["Shifts_DietaryRequiredBeforeSignup"].Value);
            return RedirectHeader(Url.Action(
                "DietaryMedical", "Profile", new { returnAction = "signup", shiftId }));
        }

        // Narrow flag drives SignUpAsync's auto-confirm path (admin/approver only).
        var privileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User);
        SignupResult result;
        bool signedUp;
        if (existing is not null)
        {
            result = await signupService.BailAsync(existing.Id, user.Id, "self-service toggle");
            signedUp = false;
        }
        else
        {
            result = await signupService.SignUpAsync(
                user.Id,
                shiftId,
                flags: privileged ? ShiftSignupRequestFlags.Privileged : ShiftSignupRequestFlags.None);
            signedUp = result.Success;
        }

        // Broad privilege — matches the browse page (Index.cshtml) so a department
        // coordinator's row for an admin-only/hidden shift still renders instead of
        // falling out of the browse set and 500-ing after the write already committed.
        var canViewRestricted = privileged
            || (await shiftMgmt.GetCoordinatorTeamIdsAsync(user.Id)).Count > 0;

        var after = await signupService.GetByUserAsync(user.Id, es.Id);
        var row = await browsePageBuilder.BuildRowAsync(shiftId, after, canViewRestricted, ct);
        // Rare race (shift just closed/deleted between write and re-read): resync the
        // whole page rather than swap a missing row.
        if (row is null)
            return RedirectHeader(Url.Action(nameof(Index)));

        var value = row.Value;
        var blocked = await ComputeSignupsBlockedByMissingDietaryAsync(user, ct);

        Response.Headers["X-Signed-Up"] = signedUp ? "true" : "false";
        Response.Headers["X-My-Signup-Count"] = after
            .Count(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
            .ToString(CultureInfo.InvariantCulture);

        if (!result.Success && result.Error is not null)
            SetToastHeader("warning", result.Error);
        else if (result.Warning is not null)
            SetToastHeader("warning", result.Warning);
        else if (signedUp)
            SetToastHeader("success", value.Status == SignupStatus.Pending
                ? localizer["Shifts_Toast_Applied"].Value
                : localizer["Shifts_Toast_SignedUp"].Value);
        else
            SetToastHeader("success", localizer["Shifts_Toast_Removed"].Value);

        if (value.Item.Shift.IsAllDay)
            return PartialView("_BuildStrikeRotaRow", new BuildStrikeRotaRowViewModel
            {
                Item = value.Item,
                Es = es,
                IsSignedUp = value.IsSignedUp,
                SignupStatus = value.Status,
                SignupsBlockedByMissingDietary = blocked,
                Interaction = ShiftSignupInteraction.InstantToggle
            });

        return PartialView("_EventRotaRow", new EventRotaRowViewModel
        {
            Item = value.Item,
            Es = es,
            IsSignedUp = value.IsSignedUp,
            SignupStatus = value.Status,
            SignupsBlockedByMissingDietary = blocked,
            Interaction = ShiftSignupInteraction.InstantToggle
        });
    }

    // 204 + X-Redirect header; the client navigates. url may be null under unit
    // tests (no routing), in which case only the status code is emitted.
    private IActionResult RedirectHeader(string? url)
    {
        if (!string.IsNullOrEmpty(url)) Response.Headers["X-Redirect"] = url;
        return StatusCode(204);
    }

    private void SetToastHeader(string type, string message)
    {
        Response.Headers["X-Toast-Type"] = type;
        Response.Headers["X-Toast-Msg"] = Uri.EscapeDataString(message);
    }

    // Lockout flag shared by the dietary banner and the rota-table Sign-Up
    // buttons: true when the user has a qualifying cantina signup but no
    // recorded DietaryPreference. Computed once per request on the top-level
    // VM (Index/Mine); the views propagate it to each rota-partial. Mirrors
    // the banner's own self-check and the SignUp gate's condition. Medical
    // conditions are intentionally excluded (only DietaryPreference blocks).
    private async Task<bool> ComputeSignupsBlockedByMissingDietaryAsync(UserInfo user, CancellationToken ct = default)
    {
        if (!await shiftMgmt.HasQualifyingCantinaSignupAsync(user.Id, ct)) return false;
        return string.IsNullOrEmpty(user.Profile?.DietaryPreference);
    }

    // True when the shift qualifies for a cantina meal and the user has no DietaryPreference yet.
    private async Task<bool> ShiftNeedsDietaryFirstAsync(UserInfo user, Guid shiftId)
    {
        var shift = await shiftMgmt.GetShiftByIdAsync(shiftId);
        if (shift is null || !shift.QualifiesForCantinaMeal()) return false;
        return string.IsNullOrEmpty(user.Profile?.DietaryPreference);
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
            await signupService.BailRangeAsync(signupBlockId, user.Id);
            SetSuccess("Successfully bailed from shift range.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Failed to bail shift range {SignupBlockId} for user {UserId}", signupBlockId, user.Id);
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

        var result = await signupService.BailAsync(signupId, user.Id, reason);

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

        var es = await shiftMgmt.GetActiveAsync();

        // see #720: cached ShiftUserView, event-scoped (empty when no active event).
        var userView = await shiftView.GetUserAsync(user.Id);
        var signups = userView.Signups;

        var now = clock.GetCurrentInstant();
        var model = new MyShiftsViewModel
        {
            EventSettings = es,
            UserId = user.Id,
            SignupsBlockedByMissingDietary = await ComputeSignupsBlockedByMissingDietaryAsync(user, HttpContext.RequestAborted),
        };

        var mineTeamNames = await LoadTeamNamesForSignupsAsync(signups);
        var buckets = ShiftSignupBucketer.Build(signups, es, mineTeamNames, now, onMissingSignupData: signup =>
            logger.LogWarning(
                "Skipping shift signup {SignupId} for user {UserId} because related shift data was missing",
                signup.Id,
                user.Id));

        model.Upcoming = buckets.Upcoming;
        model.Pending = buckets.Pending;
        model.Past = buckets.Past;

        PopulateAvailability(model, userView, es);
        await EnsureICalUrlAsync(model, user);

        return View(model);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> LoadTeamNamesForSignupsAsync(IReadOnlyList<ShiftSignup> signups)
    {
        var teamIds = ShiftSignupBucketer.GetTeamIds(signups);
        if (teamIds.Count == 0)
            return new Dictionary<Guid, string>();
        var teamsById = await teamService.GetTeamsAsync();
        return teamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);
    }

    private static void PopulateAvailability(MyShiftsViewModel model, ShiftUserView userView, EventSettings? eventSettings)
    {
        if (eventSettings is null) return;

        // see #720: availability via cached ShiftUserView (null when no row for event).
        if (userView.Availability is not null)
            model.AvailableDayOffsets = userView.Availability.AvailableDayOffsets.ToList();
    }

    private async Task EnsureICalUrlAsync(MyShiftsViewModel model, UserInfo user)
    {
        var token = user.ICalToken;
        if (token is null)
        {
            token = Guid.NewGuid();
            await _userService.SetICalTokenAsync(user.Id, token.Value);
        }

        model.ICalUrl = $"{Request.Scheme}://{Request.Host}/ICal/{token}.ics";
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

        var es = await shiftMgmt.GetActiveAsync();
        if (es is null) return BadRequest("No active event.");

        await volunteerTrackingService.SetAvailabilityAsync(user.Id, es.Id, dayOffsets ?? []);
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

        var newToken = Guid.NewGuid();
        await _userService.SetICalTokenAsync(user.Id, newToken);

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

        await shiftMgmt.SetVolunteerTagPreferencesAsync(user.Id, tagIds ?? []);
        SetSuccess("Tag preferences saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Settings")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Settings()
    {
        var es = await shiftMgmt.GetActiveAsync();
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
            var existing = await shiftMgmt.GetByIdAsync(model.Id.Value);
            if (existing is null) return NotFound();

            EventSettingsFormMapper.Apply(existing, draft);
            await shiftMgmt.UpdateAsync(existing);
        }
        else
        {
            await shiftMgmt.CreateAsync(EventSettingsFormMapper.Create(draft, clock.GetCurrentInstant()));
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
        var allSignups = await signupService.GetByUserAsync(userId);
        return allSignups
            .Where(s => s.Status is SignupStatus.Pending or SignupStatus.Confirmed)
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
                    DisplayStart: DateFormattingExtensions.TimeOfDayPattern.Format(localStart.TimeOfDay),
                    DisplayEnd: DateFormattingExtensions.TimeOfDayPattern.Format(localEnd.TimeOfDay));
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

    // Admin diagnostic: signups with no Created/Voluntold/Confirmed audit row
    // (legacy Pending self-signups that bypassed Confirm before Bail/Refuse/Cancel).

    [HttpGet("OrphanSignups")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> OrphanSignups(CancellationToken ct)
    {
        var allSignups = await signupService.GetAllForOrphanScanAsync(ct);
        var auditedIds = await auditLogService.GetEntityIdsForEntityTypeActionsAsync(
            nameof(ShiftSignup),
            [AuditAction.ShiftSignupCreated, AuditAction.ShiftSignupVoluntold, AuditAction.ShiftSignupConfirmed],
            ct);

        var orphans = allSignups.Where(s => !auditedIds.Contains(s.Id)).ToList();
        var users = await ResolveOrphanActorsAsync(orphans, _userService, ct);
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
        // §2c: names via IUserService (this isn't a render-the-audit-log view).
        var userIds = orphans
            .Select(s => s.UserId)
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
        string? GetName(Guid? id) => id.HasValue && users.TryGetValue(id.Value, out var u) ? u.BurnerName : null;

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
                EnrolledByUserId: s.EnrolledByUserId,
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
    Guid? EnrolledByUserId,
    Guid? SignupBlockId);

public record OrphanSignupsViewModel(
    int TotalSignups,
    int OrphanCount,
    int UniqueUsers,
    IReadOnlyList<OrphanSignupRow> Rows);
