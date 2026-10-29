using Humans.Base.Controllers;
using System.Globalization;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using Humans.Onboarding;
using Humans.Base.Authorization;
using Humans.Shifts.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Shifts.Controllers;

[Authorize]
[Route("Shifts")]
internal sealed class ShiftsController(
    IShiftManagement shiftMgmt,
    IBurnSettingsService burnSettings,
    IShiftSignupService signupService,
    IVolunteerTrackingManagement volunteerTrackingService,
    IShiftRowView shiftView,
    ITeamService teamService,
    IAuditLogService auditLogService,
    IUserService userService,
    IStringLocalizer<ShiftsResource> localizer,
    // The name-gate message is Onboarding's copy, rendered from here.
    IStringLocalizer<OnboardingResource> onboardingLocalizer,
    IClock clock,
    ShiftBrowsePageBuilder browsePageBuilder,
    ILogger<ShiftsController> logger) : HumansControllerBase(userService)
{
    private readonly IUserService _userService = userService;

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? departmentId, string? fromDate, string? toDate, string? period, string? day = null, bool showFull = false, [FromQuery(Name = "tags")] List<Guid>? tagIds = null, string? sort = null, [FromQuery(Name = "periods")] List<string>? periods = null)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync(HttpContext.RequestAborted);
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        if (RedirectIfNameMissing(user) is { } nameGate) return nameGate;

        var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
        if (es is null) return View("NoActiveEvent");

        var isPrivileged = ShiftsRoles.IsPrivilegedSignupApprover(User) ||
                           (await shiftMgmt.GetCoordinatorTeamIdsAsync(user.Id, HttpContext.RequestAborted)).Count > 0;

        var userView = await shiftView.GetUserAsync(user.Id, HttpContext.RequestAborted);
        var userSignups = userView.Signups;
        var hasSignups = userSignups.Count > 0;
        var userActiveSignupsForUi = await LoadUserActiveSignupsForUiAsync(user.Id);

        if (!es.IsShiftBrowsingOpen && !isPrivileged && !hasSignups)
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
            day,
            showFull,
            tagIds,
            sort,
            periods,
            isPrivileged),
            HttpContext.RequestAborted);

        model.UserId = user.Id;
        model.EarlyEntrySignupsClosed = es.IsEarlyEntrySignupsClosedFor(isPrivileged, clock.GetCurrentInstant());

        return View(model);
    }

    // Signup needs the legal name for the rota report.
    private IActionResult? RedirectIfNameMissing(UserInfo user)
    {
        if (user.HasRequiredNameFields) return null;
        SetInfo(onboardingLocalizer["Onboarding_NameRequiredBeforeShifts"].Value);
        return RedirectToAction("Index", "OnboardingWidget");
    }

    // One policy across all three scopes: the global page exposes the superset, so the narrower ones share it.
    [HttpGet("Summary")]
    [Authorize(Policy = PolicyNames.ShiftDepartmentManager)]
    public Task<IActionResult> Summary() => RenderSummaryAsync(null, null);

    [HttpGet("Summary/{teamSlug}")]
    [Authorize(Policy = PolicyNames.ShiftDepartmentManager)]
    public Task<IActionResult> SummaryTeam(string teamSlug) => RenderSummaryAsync(teamSlug, null);

    [HttpGet("Summary/{teamSlug}/{rotaGuid:guid}")]
    [Authorize(Policy = PolicyNames.ShiftDepartmentManager)]
    public Task<IActionResult> SummaryRota(string teamSlug, Guid rotaGuid) =>
        RenderSummaryAsync(teamSlug, rotaGuid);

    private async Task<IActionResult> RenderSummaryAsync(string? teamSlug, Guid? rotaId)
    {
        var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
        if (es is null) return View("NoActiveEvent");

        var summary = await shiftMgmt.BuildSummaryAsync(es, teamSlug, rotaId, HttpContext.RequestAborted);
        if (summary is null) return NotFound();

        var model = new ShiftSummaryViewModel
        {
            EventName = es.EventName,
            Scope = summary.Scope,
            TeamName = summary.TeamName,
            TeamSlug = summary.TeamSlug,
            RotaName = summary.RotaName,
            RotaId = summary.RotaId,
            Humans = summary.Humans
                .OrderByDescending(h => h.Hours)
                .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            // Most hours first; the campless row last.
            Camps = summary.Camps
                .OrderBy(c => c.CampId is null)
                .ThenByDescending(c => c.Hours)
                .ThenBy(c => c.CampName, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            TeamLinks = summary.TeamLinks
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            RotaLinks = summary.RotaLinks
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        };

        return View("Summary", model);
    }

    // The outcome and new signup count travel back as headers (X-Signed-Up, X-My-Signup-Count, X-Toast-*);
    // the name gate returns 204 with X-Redirect so the client navigates instead of swapping a row.
    [HttpPost("ToggleDay")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleDay(Guid shiftId, CancellationToken ct)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null) return currentUserNotFound;

        if (RedirectIfNameMissing(user) is not null)
            return RedirectHeader(Url.Action(
                "Index", "OnboardingWidget"));

        var es = await burnSettings.GetActiveAsync(ct);
        if (es is null)
            return RedirectHeader(Url.Action(nameof(Index)));

        // Narrow flag for auto-confirm. The service widens it to CanViewRestricted, as the browse page does, so a
        // coordinator's row for a hidden shift still renders instead of 500-ing after the write committed.
        var privileged = ShiftsRoles.IsPrivilegedSignupApprover(User);
        var outcome = await signupService.ToggleDayAsync(user.Id, shiftId, es.Id, privileged, ct);

        var result = outcome.Result;
        var row = await browsePageBuilder.BuildRowAsync(shiftId, outcome.SignupsAfter, outcome.CanViewRestricted, ct);
        // The shift closed or vanished between write and re-read: resync the page rather than swap a missing row.
        if (row is null)
            return RedirectHeader(Url.Action(nameof(Index)));

        var value = row.Value;

        Response.Headers["X-Signed-Up"] = outcome.SignedUp ? "true" : "false";
        Response.Headers["X-My-Signup-Count"] = outcome.ActiveSignupCount.ToString(CultureInfo.InvariantCulture);

        if (!result.Success && result.Error is not null)
            SetToastHeader("warning", result.Error);
        else if (result.Warning is not null)
            SetToastHeader("warning", result.Warning);
        else if (outcome.SignedUp)
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
                EarlyEntrySignupsClosed = es.IsEarlyEntrySignupsClosedFor(outcome.CanViewRestricted, clock.GetCurrentInstant()),
                Interaction = ShiftSignupInteraction.InstantToggle
            });

        return PartialView("_EventRotaRow", new EventRotaRowViewModel
        {
            Item = value.Item,
            Es = es,
            IsSignedUp = value.IsSignedUp,
            SignupStatus = value.Status,
            EarlyEntrySignupsClosed = es.IsEarlyEntrySignupsClosedFor(outcome.CanViewRestricted, clock.GetCurrentInstant()),
            Interaction = ShiftSignupInteraction.InstantToggle
        });
    }

    // url is null under unit tests (no routing); then only the status code goes out.
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

    [HttpPost("BailRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BailRange(Guid signupBlockId)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var result = await signupService.BailRangeAsync(signupBlockId, user.Id);
        if (result.ErrorKey is { } errorKey)
        {
            logger.LogWarning("Rejected shift range bail {SignupBlockId} for user {UserId}: {Reason}", signupBlockId, user.Id, errorKey);
            SetError(localizer[errorKey].Value);
        }
        else
        {
            SetSuccess(localizer["Shifts_BailRangeSuccess"].Value);
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
            SetError(result.Error ?? localizer["Shifts_BailFailed"].Value);
            return RedirectToAction(nameof(Mine));
        }

        SetSuccess(localizer["Shifts_BailSuccess"].Value);
        return RedirectToAction(nameof(Mine));
    }

    [HttpGet("Mine")]
    public async Task<IActionResult> Mine()
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync(HttpContext.RequestAborted);
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);

        var userView = await shiftView.GetUserAsync(user.Id, HttpContext.RequestAborted);
        var signups = userView.Signups;

        var now = clock.GetCurrentInstant();
        var model = new MyShiftsViewModel
        {
            EventSettings = es,
            UserId = user.Id,
        };

        var teamIds = ShiftSignupBucketer.GetTeamIds(signups);

        // Mirrors the per-team bail gate in ShiftSignupService: after early-entry close only those who can approve
        // for the signup's own department may bail. Checked once per distinct team, not per row.
        var bailLockedTeamIds = new HashSet<Guid>();
        if (es is not null && es.IsEarlyEntryClosed(now))
        {
            foreach (var teamId in teamIds)
            {
                if (!await shiftMgmt.CanApproveSignupsAsync(user.Id, teamId, HttpContext.RequestAborted))
                    bailLockedTeamIds.Add(teamId);
            }
        }

        IReadOnlyDictionary<Guid, string> mineTeamNames = new Dictionary<Guid, string>();
        if (teamIds.Count > 0)
        {
            var teamsById = await teamService.GetTeamsAsync(HttpContext.RequestAborted);
            mineTeamNames = teamIds
                .Where(teamsById.ContainsKey)
                .ToDictionary(id => id, id => teamsById[id].Name);
        }
        var buckets = ShiftSignupBucketer.Build(signups, es, mineTeamNames, now, onMissingSignupData: signup =>
            logger.LogWarning(
                "Skipping shift signup {SignupId} for user {UserId} because related shift data was missing",
                signup.Id,
                user.Id));

        foreach (var item in buckets.Upcoming.Concat(buckets.Pending))
            item.BailLocked = item.Signup.Shift.IsEarlyEntry &&
                              bailLockedTeamIds.Contains(item.Signup.Shift.Rota.TeamId);

        model.Upcoming = buckets.Upcoming;
        model.Pending = buckets.Pending;
        model.Past = buckets.Past;

        if (es is not null && userView.Availability is not null)
            model.AvailableDayOffsets = userView.Availability.AvailableDayOffsets.ToList();

        return View(model);
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

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed volunteer availability form for {UserId}", user.Id);
            return BadRequest(ModelState);
        }

        var es = await burnSettings.GetActiveAsync(HttpContext.RequestAborted);
        if (es is null) return BadRequest(localizer["Shifts_VolTrack_NoActiveEvent"].Value);

        if (!await volunteerTrackingService.SetAvailabilityAsync(user.Id, es.Id, dayOffsets ?? []))
        {
            logger.LogWarning("Rejected volunteer availability outside event {EventId} for {UserId}", es.Id, user.Id);
            return BadRequest(localizer["Shifts_AvailabilityInvalidDays"].Value);
        }
        SetSuccess(localizer["Shifts_AvailabilityUpdated"].Value);
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

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed volunteer tag preferences form for {UserId}", user.Id);
            return BadRequest(ModelState);
        }

        await shiftMgmt.SetVolunteerTagPreferencesAsync(user.Id, tagIds ?? []);
        SetSuccess(localizer["Shifts_TagPreferencesSaved"].Value);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Settings(EventSettingsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            // Name the broken rule: a bare "invalid" leaves the operator guessing which field to fix.
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).ToList();
            SetError(errors.Count > 0 ? string.Join(" ", errors) : "Invalid event settings.");
            return Redirect("/Settings#shifts");
        }

        if (GetCurrentUserId() is not { } actorId) return Challenge();

        // Shifts keeps only its own knobs, created on demand for Settings' active event.
        var active = await burnSettings.GetActiveAsync();
        if (active is null)
        {
            SetError("No active event configured — set one at /Settings#event first.");
            return Redirect("/Settings#shifts");
        }

        await shiftMgmt.SaveKnobsAsync(
            active.Id, model.IsShiftBrowsingOpen, model.GlobalVolunteerCap, model.ReminderLeadTimeHours,
            new BuildSubPeriodStarts(model.FirstCrewStartOffset, model.SetupWeekStartOffset,
                model.PreEventWeekStartOffset, model.FinishingWeekendStartOffset),
            actorId);

        SetSuccess("Event settings saved.");
        return Redirect("/Settings#shifts");
    }

    // Signups can span cycles, so each renders against its own rota's burn, resolved once up front: a lookup in
    // the projection would be an N+1.
    private async Task<IReadOnlyList<UserSignupConflictItem>> LoadUserActiveSignupsForUiAsync(Guid userId)
    {
        var ct = HttpContext.RequestAborted;
        var active = (await signupService.GetByUserAsync(userId, ct: ct))
            .Where(s => s.IsActive)
            .ToList();

        var burns = new Dictionary<Guid, BurnSettingsInfo>();
        foreach (var burnId in active.Select(s => s.EventSettingsId).Distinct())
        {
            if (await burnSettings.GetByIdAsync(burnId, ct) is { } burn)
                burns[burnId] = burn;
        }

        var items = new List<UserSignupConflictItem>(active.Count);
        foreach (var s in active)
        {
            // No burn: drop the row rather than render a bogus time.
            if (!burns.TryGetValue(s.EventSettingsId, out var sEs))
                continue;

            var absStart = s.AbsoluteStart;
            var absEnd = s.AbsoluteEnd;
            var tz = DateTimeZoneProviders.Tzdb[sEs.TimeZoneId];
            var localStart = absStart.InZone(tz).LocalDateTime;
            var localEnd = absEnd.InZone(tz).LocalDateTime;
            items.Add(new UserSignupConflictItem(
                Date: localStart.Date,
                RotaName: s.RotaName,
                AbsoluteStart: absStart,
                AbsoluteEnd: absEnd,
                DisplayStart: DateFormattingExtensions.TimeOfDayPattern.Format(localStart.TimeOfDay),
                DisplayEnd: DateFormattingExtensions.TimeOfDayPattern.Format(localEnd.TimeOfDay)));
        }

        return items;
    }

    // Signups with no Created/Voluntold/Confirmed audit row: Pending self-signups that skipped Confirm.
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

internal sealed record OrphanSignupRow(
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

internal sealed record OrphanSignupsViewModel(
    int TotalSignups,
    int OrphanCount,
    int UniqueUsers,
    IReadOnlyList<OrphanSignupRow> Rows);
