using System.Text.Json;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
    private readonly IClock _clock;
    private readonly ILogger<ShiftsController> _logger;

    public ShiftsController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        ITeamService teamService,
        IAuditLogService auditLogService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<ShiftsController> logger)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _teamService = teamService;
        _auditLogService = auditLogService;
        _clock = clock;
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

        if (!es.IsShiftBrowsingOpen && !isPrivileged && !hasSignups)
            return View("BrowsingClosed");

        var (userSignupShiftIds, userSignupStatuses) = ShiftSignupHelper.ResolveActiveStatuses(userSignups);

        // Parse date range filters
        LocalDate? filterFromDate = null;
        LocalDate? filterToDate = null;
        if (!string.IsNullOrEmpty(fromDate) && LocalDatePattern.Iso.Parse(fromDate) is { Success: true } fromResult)
            filterFromDate = fromResult.Value;
        if (!string.IsNullOrEmpty(toDate) && LocalDatePattern.Iso.Parse(toDate) is { Success: true } toResult)
            filterToDate = toResult.Value;

        // Explicit dates take precedence over period tab — prevents conflicting filters
        // (e.g., user picks a date outside the active period's range)
        if ((!string.IsNullOrEmpty(fromDate) || !string.IsNullOrEmpty(toDate)) && !string.IsNullOrEmpty(period))
            period = null;

        // Multiselect period support: if specific periods are provided, compute the union date range.
        // When all 3 are selected (or none), treat as "no filter" — same as legacy single-period "All".
        var activePeriods = (periods ?? [])
            .Where(p => Enum.TryParse<ShiftPeriod>(p, true, out _))
            .Select(p => Enum.Parse<ShiftPeriod>(p, true))
            .Distinct()
            .ToList();
        var allPeriodsSelected = activePeriods.Count == 3 ||
            (activePeriods.Count == 0 && string.IsNullOrEmpty(period));

        // Legacy single-period param: fold into activePeriods for unified handling
        if (activePeriods.Count == 0 && !string.IsNullOrEmpty(period) &&
            Enum.TryParse<ShiftPeriod>(period, true, out var parsedPeriod))
        {
            activePeriods = [parsedPeriod];
        }

        // Apply period filter — for single contiguous period, use date range query.
        // For multi-period or non-contiguous, fetch all and post-filter by shift period.
        var postFilterByPeriod = false;
        if (activePeriods.Count > 0 && !allPeriodsSelected)
        {
            if (activePeriods.Count == 1)
            {
                // Single period: use efficient date range query
                var (periodFrom, periodTo) = GetPeriodDateRange(es, activePeriods[0]);
                filterFromDate ??= periodFrom;
                filterToDate ??= periodTo;
            }
            else
            {
                // Multiple non-contiguous periods (e.g., Build + Strike): fetch all, filter after
                postFilterByPeriod = true;
            }
        }

        // Build the browse view — show all active shifts, hide AdminOnly from regular volunteers.
        // Signups are loaded unconditionally so the public avatar-chip column has data.
        var urgentShifts = await _shiftMgmt.GetBrowseShiftsAsync(
            es.Id, departmentId: departmentId,
            fromDate: filterFromDate, toDate: filterToDate,
            includeAdminOnly: isPrivileged, includeSignups: true,
            includeHidden: isPrivileged);

        // Post-filter by period when multiple non-contiguous periods are selected (e.g., Build + Strike)
        var periodFilteredShifts = postFilterByPeriod
            ? urgentShifts.Where(u => activePeriods.Contains(u.Shift.GetShiftPeriod(es))).ToList()
            : (IReadOnlyList<UrgentShift>)urgentShifts;

        // Apply tag filter — keep only shifts whose rota has at least one of the selected tags
        var activeTagFilter = tagIds?.Where(id => id != Guid.Empty).ToList() ?? [];
        var filteredShifts = activeTagFilter.Count > 0
            ? periodFilteredShifts.Where(u => u.Shift.Rota.Tags.Any(t => activeTagFilter.Contains(t.Id))).ToList()
            : periodFilteredShifts;

        // Resolve team name/slug cross-section (Shifts doesn't own Team data).
        var shiftTeamIds = filteredShifts.Select(u => u.Shift.Rota.TeamId).Distinct().ToList();
        var teamLookup = await _teamService.GetByIdsWithParentsAsync(shiftTeamIds);

        // Map UrgentShift → ShiftDisplayItem (shared by both sort modes)
        ShiftDisplayItem MapToDisplayItem(UrgentShift u)
        {
            var (start, end, shiftPeriod) = _shiftMgmt.ResolveShiftTimes(u.Shift, es);
            return new ShiftDisplayItem
            {
                Shift = u.Shift,
                AbsoluteStart = start,
                AbsoluteEnd = end,
                Period = shiftPeriod,
                ConfirmedCount = u.ConfirmedCount,
                RemainingSlots = u.RemainingSlots,
                UrgencyScore = u.UrgencyScore,
                Signups = u.Signups
                    .Select(s => new ShiftSignupInfo(
                        s.UserId, s.DisplayName, s.Status,
                        s.HasProfilePicture ? $"/Profile/Picture?id={s.UserId}" : null))
                    .ToList()
            };
        }

        RotaShiftGroup BuildRotaGroup(IGrouping<Guid, UrgentShift> rotaGroup, string? deptName = null, string? deptSlug = null)
        {
            var rota = rotaGroup.OrderBy(x => x.Shift.Id).First().Shift.Rota;
            var shifts = rotaGroup
                .Select(MapToDisplayItem)
                .OrderBy(s => s.AbsoluteStart)
                .ToList();
            return new RotaShiftGroup
            {
                Rota = rota,
                Shifts = shifts,
                DepartmentName = deptName,
                DepartmentSlug = deptSlug,
                MaxUrgencyScore = shifts.Count > 0 ? shifts.Max(s => s.UrgencyScore) : 0,
                TotalConfirmed = shifts.Sum(s => s.ConfirmedCount),
                TotalSlots = shifts.Sum(s => s.Shift.MaxVolunteers)
            };
        }

        // Group by department → rota → shift
        var departments = filteredShifts
            .GroupBy(u => u.Shift.Rota.TeamId)
            .Select(deptGroup =>
            {
                var firstShift = deptGroup.OrderBy(x => x.Shift.Id).First().Shift;
                var team = teamLookup.TryGetValue(firstShift.Rota.TeamId, out var t) ? t : null;
                var deptName = team?.Name ?? string.Empty;
                var deptSlug = team?.Slug ?? string.Empty;
                return new DepartmentShiftGroup
                {
                    TeamId = firstShift.Rota.TeamId,
                    TeamName = deptName,
                    TeamDescription = team?.Description,
                    TeamSlug = deptSlug,
                    Rotas = deptGroup
                        .GroupBy(u => u.Shift.RotaId)
                        .Select(rg => BuildRotaGroup(rg, deptName, deptSlug))
                        .OrderBy(r => r.Rota.Name, StringComparer.Ordinal)
                        .ToList()
                };
            })
            .OrderBy(d => d.TeamName, StringComparer.Ordinal)
            .ToList();

        // Build urgency-ranked flat rota list — default sort is now "urgency" (most needed first)
        var isUrgencySort = !string.Equals(sort, "department", StringComparison.OrdinalIgnoreCase);
        var urgencyRankedRotas = isUrgencySort
            ? departments.SelectMany(d => d.Rotas)
                .OrderByDescending(r => r.MaxUrgencyScore)
                .ToList()
            : [];

        // Get department list for filter dropdown — if already unfiltered, reuse data
        List<DepartmentOption> allDepartments;
        if (!departmentId.HasValue)
        {
            allDepartments = departments
                .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName })
                .ToList();
        }
        else
        {
            var depts = await _shiftMgmt.GetDepartmentsWithRotasAsync(es.Id);
            allDepartments = depts
                .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName })
                .ToList();
        }

        // Load all tags for filter UI and volunteer's preferred tags
        var allTags = await _shiftMgmt.GetAllTagsAsync();
        var userPreferredTags = await _shiftMgmt.GetVolunteerTagPreferencesAsync(user.Id);

        var model = new ShiftBrowseViewModel
        {
            EventSettings = es,
            FilterDepartmentId = departmentId,
            FilterFromDate = fromDate,
            FilterToDate = toDate,
            FilterPeriod = period,
            FilterPeriods = activePeriods.Select(p => p.ToString()).ToList(),
            ShowFullShifts = showFull,
            UserSignupShiftIds = userSignupShiftIds,
            UserSignupStatuses = userSignupStatuses,
            Departments = departments,
            AllDepartments = allDepartments,
            // Temporarily public — signups list visible to all browsers. Keep the isPrivileged
            // computation in place so we can flip back to `ShowSignups = isPrivileged` if folks object.
            ShowSignups = true,
            Sort = isUrgencySort ? "urgency" : "department",
            UrgencyRankedRotas = urgencyRankedRotas,
            AllTags = allTags.ToList(),
            FilterTagIds = activeTagFilter,
            UserPreferredTagIds = userPreferredTags.Select(t => t.Id).ToHashSet(),
            MySignupCount = userSignups.Count(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
        };

        return View(model);
    }

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

        if (!result.Success)
        {
            SetError(result.Error ?? "Shift signup failed.");
            return RedirectToAction(nameof(Index), BuildFilterRouteValues(departmentId, fromDate, toDate, period, tagIds, sort: sort, periods: periods));
        }

        SetSuccess(result.Warning is not null
            ? $"Signed up successfully. Note: {result.Warning}"
            : "Signed up successfully!");

        return RedirectToAction(nameof(Index), BuildFilterRouteValues(departmentId, fromDate, toDate, period, tagIds, sort: sort, periods: periods));
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
        var result = await _signupService.SignUpRangeAsync(user.Id, rotaId, startDayOffset, endDayOffset, isPrivileged: privileged);

        if (!result.Success)
        {
            SetError(result.Error ?? "Shift range signup failed.");
            return RedirectToAction(nameof(Index), BuildFilterRouteValues(departmentId, fromDate, toDate, period, tagIds, sort: sort, periods: periods));
        }

        SetSuccess(result.Warning is not null
            ? $"Signed up for date range. Note: {result.Warning}"
            : "Signed up for date range!");

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

        var mineTeamIds = signups
            .Where(s => s.Shift?.Rota is not null)
            .Select(s => s.Shift.Rota.TeamId)
            .Distinct()
            .ToList();
        var mineTeamNames = mineTeamIds.Count == 0
            ? (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>()
            : await _teamService.GetTeamNamesByIdsAsync(mineTeamIds);

        foreach (var signup in signups)
        {
            if (signup.Shift?.Rota is null || es is null)
            {
                _logger.LogWarning(
                    "Skipping shift signup {SignupId} for user {UserId} because related shift data was missing",
                    signup.Id,
                    user.Id);
                continue;
            }

            var item = new MySignupItem
            {
                Signup = signup,
                DepartmentName = mineTeamNames.GetValueOrDefault(signup.Shift.Rota.TeamId, "Unknown"),
                AbsoluteStart = signup.Shift.GetAbsoluteStart(es),
                AbsoluteEnd = signup.Shift.GetAbsoluteEnd(es)
            };

            switch (signup.Status)
            {
                case SignupStatus.Confirmed when item.AbsoluteEnd > now:
                    model.Upcoming.Add(item);
                    break;
                case SignupStatus.Pending:
                    model.Pending.Add(item);
                    break;
                default:
                    model.Past.Add(item);
                    break;
            }
        }

        model.Upcoming = model.Upcoming.OrderBy(s => s.AbsoluteStart).ToList();
        model.Pending = model.Pending.OrderBy(s => s.AbsoluteStart).ToList();
        model.Past = model.Past.OrderByDescending(s => s.AbsoluteStart).ToList();

        // Load general availability
        if (es is not null)
        {
            var availability = await _availabilityService.GetByUserAsync(user.Id, es.Id);
            if (availability is not null)
                model.AvailableDayOffsets = availability.AvailableDayOffsets;
        }

        // Generate iCal token on first access
        if (user.ICalToken is null)
        {
            user.ICalToken = Guid.NewGuid();
            await UpdateCurrentUserAsync(user);
        }
        model.ICalUrl = $"{Request.Scheme}://{Request.Host}/ICal/{user.ICalToken}.ics";

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
        var model = new EventSettingsViewModel();

        if (es is not null)
        {
            model.Id = es.Id;
            model.EventName = es.EventName;
            model.TimeZoneId = es.TimeZoneId;
            model.GateOpeningDate = LocalDatePattern.Iso.Format(es.GateOpeningDate);
            model.BuildStartOffset = es.BuildStartOffset;
            model.EventEndOffset = es.EventEndOffset;
            model.StrikeEndOffset = es.StrikeEndOffset;
            model.EarlyEntryCapacityJson = JsonSerializer.Serialize(es.EarlyEntryCapacity);
            model.BarriosEarlyEntryAllocationJson = es.BarriosEarlyEntryAllocation is not null
                ? JsonSerializer.Serialize(es.BarriosEarlyEntryAllocation)
                : null;
            model.EarlyEntryClose = es.EarlyEntryClose.HasValue
                ? InstantPattern.General.Format(es.EarlyEntryClose.Value)
                : null;
            model.IsShiftBrowsingOpen = es.IsShiftBrowsingOpen;
            model.GlobalVolunteerCap = es.GlobalVolunteerCap;
            model.ReminderLeadTimeHours = es.ReminderLeadTimeHours;
            model.IsActive = es.IsActive;
        }

        return View(model);
    }

    [HttpPost("Settings")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    public async Task<IActionResult> Settings(EventSettingsViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(model.TimeZoneId) is null)
        {
            ModelState.AddModelError(nameof(model.TimeZoneId), "Invalid IANA timezone ID.");
            return View(model);
        }

        var parsedDate = LocalDatePattern.Iso.Parse(model.GateOpeningDate);
        if (!parsedDate.Success)
        {
            ModelState.AddModelError(nameof(model.GateOpeningDate), "Invalid date format.");
            return View(model);
        }

        Instant? earlyEntryClose = null;
        if (!string.IsNullOrEmpty(model.EarlyEntryClose))
        {
            var parsedInstant = InstantPattern.General.Parse(model.EarlyEntryClose);
            if (!parsedInstant.Success)
            {
                ModelState.AddModelError(nameof(model.EarlyEntryClose), "Invalid UTC instant format.");
                return View(model);
            }

            earlyEntryClose = parsedInstant.Value;
        }

        var eeCapacity = !string.IsNullOrEmpty(model.EarlyEntryCapacityJson)
            ? JsonSerializer.Deserialize<Dictionary<int, int>>(model.EarlyEntryCapacityJson) ?? new()
            : new Dictionary<int, int>();

        Dictionary<int, int>? barriosAllocation = null;
        if (!string.IsNullOrEmpty(model.BarriosEarlyEntryAllocationJson))
            barriosAllocation = JsonSerializer.Deserialize<Dictionary<int, int>>(model.BarriosEarlyEntryAllocationJson);

        if (model.Id.HasValue)
        {
            var existing = await _shiftMgmt.GetByIdAsync(model.Id.Value);
            if (existing is null) return NotFound();

            existing.EventName = model.EventName;
            existing.TimeZoneId = model.TimeZoneId;
            existing.GateOpeningDate = parsedDate.Value;
            existing.Year = parsedDate.Value.Year;
            existing.BuildStartOffset = model.BuildStartOffset;
            existing.EventEndOffset = model.EventEndOffset;
            existing.StrikeEndOffset = model.StrikeEndOffset;
            existing.EarlyEntryCapacity = eeCapacity;
            existing.BarriosEarlyEntryAllocation = barriosAllocation;
            existing.EarlyEntryClose = earlyEntryClose;
            existing.IsShiftBrowsingOpen = model.IsShiftBrowsingOpen;
            existing.GlobalVolunteerCap = model.GlobalVolunteerCap;
            existing.ReminderLeadTimeHours = model.ReminderLeadTimeHours;
            existing.IsActive = model.IsActive;

            await _shiftMgmt.UpdateAsync(existing);
        }
        else
        {
            var entity = new EventSettings
            {
                Id = Guid.NewGuid(),
                EventName = model.EventName,
                TimeZoneId = model.TimeZoneId,
                GateOpeningDate = parsedDate.Value,
                BuildStartOffset = model.BuildStartOffset,
                EventEndOffset = model.EventEndOffset,
                StrikeEndOffset = model.StrikeEndOffset,
                EarlyEntryCapacity = eeCapacity,
                BarriosEarlyEntryAllocation = barriosAllocation,
                EarlyEntryClose = earlyEntryClose,
                IsShiftBrowsingOpen = model.IsShiftBrowsingOpen,
                GlobalVolunteerCap = model.GlobalVolunteerCap,
                ReminderLeadTimeHours = model.ReminderLeadTimeHours,
                IsActive = model.IsActive,
                Year = parsedDate.Value.Year,
                CreatedAt = _clock.GetCurrentInstant()
            };

            await _shiftMgmt.CreateAsync(entity);
        }

        SetSuccess("Event settings saved.");
        return RedirectToAction(nameof(Settings));
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
    public async Task<IActionResult> OrphanSignups(CancellationToken ct)
    {
        var allSignups = await _signupService.GetAllForOrphanScanAsync(ct);
        var auditedIds = await _auditLogService.GetEntityIdsForEntityTypeActionsAsync(
            nameof(ShiftSignup),
            [
                AuditAction.ShiftSignupCreated,
                AuditAction.ShiftSignupVoluntold,
                AuditAction.ShiftSignupConfirmed,
            ],
            ct);

        var orphans = allSignups
            .Where(s => !auditedIds.Contains(s.Id))
            .ToList();

        var userIds = orphans
            .SelectMany(s => new[] { s.UserId, s.ReviewedByUserId, s.EnrolledByUserId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var userDisplayNames = await _auditLogService.GetUserDisplayNamesAsync(userIds, ct);

        var rows = orphans
            .Select(s => new OrphanSignupRow(
                SignupId: s.Id,
                UserId: s.UserId,
                UserDisplayName: userDisplayNames.GetValueOrDefault(s.UserId) ?? s.UserId.ToString(),
                RotaName: s.Shift.Rota.Name,
                ShiftDate: s.Shift.Rota.EventSettings.GateOpeningDate.PlusDays(s.Shift.DayOffset),
                Status: s.Status,
                CreatedAt: s.CreatedAt,
                ReviewedByUserId: s.ReviewedByUserId,
                ReviewedByDisplayName: s.ReviewedByUserId.HasValue
                    ? userDisplayNames.GetValueOrDefault(s.ReviewedByUserId.Value)
                    : null,
                EnrolledByUserId: s.EnrolledByUserId,
                EnrolledByDisplayName: s.EnrolledByUserId.HasValue
                    ? userDisplayNames.GetValueOrDefault(s.EnrolledByUserId.Value)
                    : null,
                SignupBlockId: s.SignupBlockId))
            .OrderBy(r => r.UserDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.CreatedAt)
            .ToList();

        var vm = new OrphanSignupsViewModel(
            TotalSignups: allSignups.Count,
            OrphanCount: rows.Count,
            UniqueUsers: rows.Select(r => r.UserId).Distinct().Count(),
            Rows: rows);

        return View(vm);
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
