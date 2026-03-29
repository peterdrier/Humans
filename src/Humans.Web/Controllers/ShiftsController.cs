using System.Text.Json;
using Humans.Application.Interfaces;
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
    private readonly IClock _clock;
    private readonly ILogger<ShiftsController> _logger;

    public ShiftsController(
        IShiftManagementService shiftMgmt,
        IShiftSignupService signupService,
        IGeneralAvailabilityService availabilityService,
        UserManager<User> userManager,
        IClock clock,
        ILogger<ShiftsController> logger)
        : base(userManager)
    {
        _shiftMgmt = shiftMgmt;
        _signupService = signupService;
        _availabilityService = availabilityService;
        _clock = clock;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid? departmentId, string? fromDate, string? toDate, string? period, bool showFull = false)
    {
        var (currentUserNotFound, user) = await ResolveCurrentUserOrChallengeAsync();
        if (currentUserNotFound is not null)
        {
            return currentUserNotFound;
        }

        var es = await _shiftMgmt.GetActiveAsync();
        if (es is null) return View("NoActiveEvent");

        var isPrivileged = ShiftRoleChecks.IsPrivilegedSignupApprover(User) ||
                           (await _shiftMgmt.GetCoordinatorDepartmentIdsAsync(user.Id)).Count > 0;

        var userSignups = await _signupService.GetByUserAsync(user.Id, es.Id);
        var hasSignups = userSignups.Count > 0;

        if (!es.IsShiftBrowsingOpen && !isPrivileged && !hasSignups)
            return View("BrowsingClosed");

        var userSignupShiftIds = userSignups
            .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
            .Select(s => s.ShiftId)
            .ToHashSet();
        var userSignupStatuses = userSignups
            .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
            .ToDictionary(s => s.ShiftId, s => s.Status);

        // Parse date range filters
        LocalDate? filterFromDate = null;
        LocalDate? filterToDate = null;
        if (!string.IsNullOrEmpty(fromDate) && LocalDatePattern.Iso.Parse(fromDate) is { Success: true } fromResult)
            filterFromDate = fromResult.Value;
        if (!string.IsNullOrEmpty(toDate) && LocalDatePattern.Iso.Parse(toDate) is { Success: true } toResult)
            filterToDate = toResult.Value;

        // Apply period filter — compute date boundaries from EventSettings offsets
        if (!string.IsNullOrEmpty(period) && Enum.TryParse<ShiftPeriod>(period, true, out var parsedPeriod))
        {
            var (periodFrom, periodTo) = GetPeriodDateRange(es, parsedPeriod);
            filterFromDate ??= periodFrom;
            filterToDate ??= periodTo;
        }

        // Build the browse view — show all active shifts, hide AdminOnly from regular volunteers
        var urgentShifts = await _shiftMgmt.GetBrowseShiftsAsync(
            es.Id, departmentId: departmentId,
            fromDate: filterFromDate, toDate: filterToDate,
            includeAdminOnly: isPrivileged, includeSignups: isPrivileged,
            includeHidden: isPrivileged);

        // Group by department → rota → shift
        var departments = urgentShifts
            .GroupBy(u => u.Shift.Rota.TeamId)
            .Select(deptGroup =>
            {
                var firstShift = deptGroup.First().Shift;
                return new DepartmentShiftGroup
                {
                    TeamId = firstShift.Rota.TeamId,
                    TeamName = firstShift.Rota.Team.Name,
                    TeamSlug = firstShift.Rota.Team.Slug,
                    Rotas = deptGroup
                        .GroupBy(u => u.Shift.RotaId)
                        .Select(rotaGroup =>
                        {
                            var rota = rotaGroup.First().Shift.Rota;
                            return new RotaShiftGroup
                            {
                                Rota = rota,
                                Shifts = rotaGroup
                                    .Select(u =>
                                    {
                                        var (start, end, period) = _shiftMgmt.ResolveShiftTimes(u.Shift, es);
                                        return new ShiftDisplayItem
                                        {
                                            Shift = u.Shift,
                                            AbsoluteStart = start,
                                            AbsoluteEnd = end,
                                            Period = period,
                                            ConfirmedCount = u.ConfirmedCount,
                                            RemainingSlots = u.RemainingSlots,
                                            Signups = u.Signups
                                                .Select(s => new ShiftSignupInfo(
                                                    s.UserId, s.DisplayName, s.Status,
                                                    s.HasProfilePicture ? $"/Human/{s.UserId}/Picture" : null))
                                                .ToList()
                                        };
                                    })
                                    .OrderBy(s => s.AbsoluteStart)
                                    .ToList()
                            };
                        })
                        .OrderBy(r => r.Rota.Name, StringComparer.Ordinal)
                        .ToList()
                };
            })
            .OrderBy(d => d.TeamName, StringComparer.Ordinal)
            .ToList();

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

        var model = new ShiftBrowseViewModel
        {
            EventSettings = es,
            FilterDepartmentId = departmentId,
            FilterFromDate = fromDate,
            FilterToDate = toDate,
            FilterPeriod = period,
            ShowFullShifts = showFull,
            UserSignupShiftIds = userSignupShiftIds,
            UserSignupStatuses = userSignupStatuses,
            Departments = departments,
            AllDepartments = allDepartments,
            ShowSignups = isPrivileged
        };

        return View(model);
    }

    [HttpPost("SignUp")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUp(Guid shiftId)
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
            return RedirectToAction(nameof(Index));
        }

        SetSuccess(result.Warning is not null
            ? $"Signed up successfully. Note: {result.Warning}"
            : "Signed up successfully!");

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("SignUpRange")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUpRange(Guid rotaId, int startDayOffset, int endDayOffset)
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
            return RedirectToAction(nameof(Index));
        }

        SetSuccess(result.Warning is not null
            ? $"Signed up for date range. Note: {result.Warning}"
            : "Signed up for date range!");

        return RedirectToAction(nameof(Index));
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

        foreach (var signup in signups)
        {
            if (signup.Shift?.Rota?.Team is null || es is null)
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
                DepartmentName = signup.Shift.Rota.Team.Name,
                AbsoluteStart = signup.Shift.GetAbsoluteStart(es),
                AbsoluteEnd = signup.Shift.GetAbsoluteEnd(es)
            };

            switch (signup.Status)
            {
                case SignupStatus.Confirmed when item.AbsoluteStart > now:
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

    [HttpGet("Settings")]
    [Authorize(Roles = RoleNames.Admin)]
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
    [Authorize(Roles = RoleNames.Admin)]
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
}
