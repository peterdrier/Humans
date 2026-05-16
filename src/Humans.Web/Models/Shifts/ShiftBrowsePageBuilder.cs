using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Models.Shifts;

/// <summary>
/// Inputs for <see cref="ShiftBrowsePageBuilder"/>. <see cref="UserTagPreferences"/>
/// is the raw <see cref="VolunteerTagPreference"/> rows from the cached
/// <c>ShiftUserView</c> (T-10, issue #720); the builder reads
/// <see cref="VolunteerTagPreference.ShiftTagId"/> to build the preferred-tag
/// id set on the view model.
/// </summary>
public sealed record ShiftBrowsePageRequest(
    EventSettings EventSettings,
    Guid UserId,
    IReadOnlyList<ShiftSignup> UserSignups,
    IReadOnlyList<VolunteerTagPreference> UserTagPreferences,
    IReadOnlyList<UserSignupConflictItem> UserActiveSignups,
    Guid? DepartmentId,
    string? FromDate,
    string? ToDate,
    string? Period,
    bool ShowFull,
    IReadOnlyList<Guid>? TagIds,
    string? Sort,
    IReadOnlyList<string>? Periods,
    bool IsPrivileged);

public sealed class ShiftBrowsePageBuilder
{
    private readonly IShiftManagementService _shiftManagement;
    private readonly ITeamService _teamService;

    public ShiftBrowsePageBuilder(IShiftManagementService shiftManagement, ITeamService teamService)
    {
        _shiftManagement = shiftManagement;
        _teamService = teamService;
    }

    public async Task<ShiftBrowseViewModel> BuildAsync(ShiftBrowsePageRequest request)
    {
        var es = request.EventSettings;
        var period = request.Period;
        var (filterFromDate, filterToDate) = ParseDateFilters(request.FromDate, request.ToDate);

        if ((!string.IsNullOrEmpty(request.FromDate) || !string.IsNullOrEmpty(request.ToDate)) && !string.IsNullOrEmpty(period))
            period = null;

        var activePeriods = ParseActivePeriods(request.Periods, period);
        var allPeriodsSelected = activePeriods.Count == 3 ||
            (activePeriods.Count == 0 && string.IsNullOrEmpty(period));

        var postFilterByPeriod = false;
        if (activePeriods.Count > 0 && !allPeriodsSelected)
        {
            if (activePeriods.Count == 1)
            {
                var (periodFrom, periodTo) = GetPeriodDateRange(es, activePeriods[0]);
                filterFromDate ??= periodFrom;
                filterToDate ??= periodTo;
            }
            else
            {
                postFilterByPeriod = true;
            }
        }

        var urgentShifts = await _shiftManagement.GetBrowseShiftsAsync(
            es.Id,
            departmentId: request.DepartmentId,
            fromDate: filterFromDate,
            toDate: filterToDate,
            includeAdminOnly: request.IsPrivileged,
            includeSignups: true,
            includeHidden: request.IsPrivileged);

        var periodFilteredShifts = postFilterByPeriod
            ? urgentShifts.Where(u => activePeriods.Contains(u.Shift.GetShiftPeriod(es))).ToList()
            : (IReadOnlyList<UrgentShift>)urgentShifts;

        var activeTagFilter = request.TagIds?.Where(id => id != Guid.Empty).ToList() ?? [];
        var filteredShifts = activeTagFilter.Count > 0
            ? periodFilteredShifts.Where(u => u.Shift.Rota.Tags.Any(t => activeTagFilter.Contains(t.Id))).ToList()
            : periodFilteredShifts;

        var departments = await BuildDepartmentGroupsAsync(filteredShifts, es);
        var isUrgencySort = !string.Equals(request.Sort, "department", StringComparison.OrdinalIgnoreCase);

        var allDepartments = await GetDepartmentOptionsAsync(request.DepartmentId, departments, es.Id);
        var allTags = await _shiftManagement.GetTagsAsync();
        // T-10: preferred tag ids come from the cached ShiftUserView's
        // TagPreferences (issue #720) — the controller already fetched the
        // view for the signup read, so we reuse those rows here. The shape
        // shift is harmless: ShiftTagPreferenceSummary.Id == ShiftTag.Id ==
        // VolunteerTagPreference.ShiftTagId.
        var (userSignupShiftIds, userSignupStatuses) = ShiftSignupHelper.ResolveActiveStatuses(request.UserSignups);

        return new ShiftBrowseViewModel
        {
            EventSettings = es,
            FilterDepartmentId = request.DepartmentId,
            FilterFromDate = request.FromDate,
            FilterToDate = request.ToDate,
            FilterPeriod = period,
            FilterPeriods = activePeriods.Select(p => p.ToString()).ToList(),
            ShowFullShifts = request.ShowFull,
            UserSignupShiftIds = userSignupShiftIds,
            UserSignupStatuses = userSignupStatuses,
            Departments = departments,
            AllDepartments = allDepartments,
            ShowSignups = true,
            Sort = isUrgencySort ? "urgency" : "department",
            UrgencyRankedRotas = isUrgencySort
                ? departments.SelectMany(d => d.Rotas).OrderByDescending(r => r.MaxUrgencyScore).ToList()
                : [],
            AllTags = allTags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            FilterTagIds = activeTagFilter,
            UserPreferredTagIds = request.UserTagPreferences.Select(t => t.ShiftTagId).ToHashSet(),
            MySignupCount = request.UserSignups.Count(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending),
            UserActiveSignups = request.UserActiveSignups
        };
    }

    private async Task<List<DepartmentShiftGroup>> BuildDepartmentGroupsAsync(
        IReadOnlyList<UrgentShift> shifts,
        EventSettings eventSettings)
    {
        var shiftTeamIds = shifts.Select(u => u.Shift.Rota.TeamId).Distinct().ToList();
        var teamLookup = await _teamService.GetByIdsWithParentsAsync(shiftTeamIds);

        return shifts
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
                        .Select(rg => ShiftBrowseMapper.BuildRotaGroup(rg, eventSettings, deptName, deptSlug))
                        .OrderBy(r => r.Rota.Name, StringComparer.Ordinal)
                        .ToList()
                };
            })
            .OrderBy(d => d.TeamName, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<DepartmentOption>> GetDepartmentOptionsAsync(
        Guid? departmentId,
        IReadOnlyList<DepartmentShiftGroup> currentDepartments,
        Guid eventSettingsId)
    {
        if (!departmentId.HasValue)
            return currentDepartments
                .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName })
                .ToList();

        var depts = await _shiftManagement.GetDepartmentsWithRotasAsync(eventSettingsId);
        return depts
            .Select(d => new DepartmentOption { TeamId = d.TeamId, Name = d.TeamName })
            .ToList();
    }

    private static (LocalDate? From, LocalDate? To) ParseDateFilters(string? fromDate, string? toDate)
    {
        LocalDate? filterFromDate = null;
        LocalDate? filterToDate = null;

        if (!string.IsNullOrEmpty(fromDate) && LocalDatePattern.Iso.Parse(fromDate) is { Success: true } fromResult)
            filterFromDate = fromResult.Value;
        if (!string.IsNullOrEmpty(toDate) && LocalDatePattern.Iso.Parse(toDate) is { Success: true } toResult)
            filterToDate = toResult.Value;

        return (filterFromDate, filterToDate);
    }

    private static List<ShiftPeriod> ParseActivePeriods(IReadOnlyList<string>? periods, string? period)
    {
        var activePeriods = (periods ?? [])
            .Where(p => Enum.TryParse<ShiftPeriod>(p, true, out _))
            .Select(p => Enum.Parse<ShiftPeriod>(p, true))
            .Distinct()
            .ToList();

        if (activePeriods.Count == 0 && !string.IsNullOrEmpty(period) &&
            Enum.TryParse<ShiftPeriod>(period, true, out var parsedPeriod))
        {
            activePeriods = [parsedPeriod];
        }

        return activePeriods;
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
