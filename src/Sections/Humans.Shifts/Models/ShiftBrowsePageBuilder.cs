using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Domain;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Text;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Models;

/// <summary>
/// Inputs for <see cref="ShiftBrowsePageBuilder"/>. <see cref="UserTagPreferences"/>
/// is the raw <see cref="VolunteerTagPreference"/> rows from the cached
/// <c>ShiftUserView</c> (T-10, issue #720); the builder reads
/// <see cref="VolunteerTagPreference.ShiftTagId"/> to build the preferred-tag
/// id set on the view model.
/// </summary>
internal sealed record ShiftBrowsePageRequest(
    BurnSettingsInfo EventSettings,
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

internal sealed class ShiftBrowsePageBuilder(
    IShiftManagementService shiftManagement,
    IBurnSettingsService burnSettings,
    ITeamServiceRead teamService)
{
    /// <summary>
    /// Active (Confirmed / Pending) signups as shift-id set + status map.
    /// <c>ShiftSignupHelper.ResolveActiveStatuses</c> is the same rule over the
    /// section's boundary shape; this one reads the section's own rows.
    /// </summary>
    private static (HashSet<Guid> ShiftIds, Dictionary<Guid, SignupStatus> Statuses) ResolveActiveStatuses(
        IReadOnlyList<ShiftSignup> signups)
    {
        var active = signups
            .Where(s => s.Status is SignupStatus.Confirmed or SignupStatus.Pending)
            .ToList();
        return (
            active.Select(s => s.ShiftId).ToHashSet(),
            active.ToDictionary(s => s.ShiftId, s => s.Status));
    }

    public async Task<ShiftBrowseViewModel> BuildAsync(ShiftBrowsePageRequest request, CancellationToken ct = default)
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
                var (periodFrom, periodTo) = ShiftFilterResolver.ResolvePeriodRange(activePeriods[0], es);
                filterFromDate ??= periodFrom;
                filterToDate ??= periodTo;
            }
            else
            {
                postFilterByPeriod = true;
            }
        }

        var browseFlags = ShiftBrowseQueryFlags.IncludeSignups;
        if (request.IsPrivileged)
            browseFlags |= ShiftBrowseQueryFlags.IncludeAdminOnly | ShiftBrowseQueryFlags.IncludeHidden;

        var urgentShifts = await shiftManagement.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            es.Id,
            request.DepartmentId,
            filterFromDate,
            filterToDate,
            browseFlags));

        var periodFilteredShifts = postFilterByPeriod
            ? urgentShifts.Where(u => activePeriods.Contains(u.Period)).ToList()
            : urgentShifts;

        var activeTagFilter = request.TagIds?.Where(id => id != Guid.Empty).ToList() ?? [];
        var filteredShifts = activeTagFilter.Count > 0
            ? periodFilteredShifts.Where(u => u.Rota.Tags.Any(t => activeTagFilter.Contains(t.Id))).ToList()
            : periodFilteredShifts;

        var departments = await BuildDepartmentGroupsAsync(filteredShifts);
        var isUrgencySort = !string.Equals(request.Sort, "department", StringComparison.OrdinalIgnoreCase);

        var allDepartments = await GetDepartmentOptionsAsync(request.DepartmentId, departments, es.Id);
        var allTags = await shiftManagement.GetTagsAsync();
        // T-10: preferred tag ids come from the cached ShiftUserView's
        // TagPreferences (issue #720) — the controller already fetched the
        // view for the signup read, so we reuse those rows here. The shape
        // shift is harmless: ShiftTagPreferenceInfo.ShiftTagId == ShiftTag.Id ==
        // VolunteerTagPreference.ShiftTagId.
        var (userSignupShiftIds, userSignupStatuses) = ResolveActiveStatuses(request.UserSignups);

        // Pies use the same date window as the shift list so the percentages
        // line up with what the user is filtering to.
        // Service returns natural-name-ordered rows; apply the display
        // grouping here so each promoted sub-team renders right after its
        // parent (memory/architecture/display-sort-in-controllers).
        var coveragePies = OrderPiesGroupedByParent(
            await shiftManagement.GetDepartmentCoveragePiesAsync(
                es.Id, filterFromDate, filterToDate, ct: ct));

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
            UserActiveSignups = request.UserActiveSignups,
            CoveragePies = coveragePies
        };
    }

    /// <summary>
    /// Rebuilds a single shift's display item plus the caller's signup state, for
    /// re-rendering one browse-table row after a signup/cancel toggle (avoids
    /// rebuilding the whole page). Resolves the active event and re-reads the shift
    /// through the same <see cref="IShiftManagementService.GetBrowseShiftsAsync"/>
    /// path <see cref="BuildAsync"/> uses, so the row's counts stay consistent.
    /// </summary>
    public async Task<(ShiftDisplayItem Item, bool IsSignedUp, SignupStatus? Status)?> BuildRowAsync(
        Guid shiftId, IReadOnlyList<ShiftSignup> userSignups, bool isPrivileged, CancellationToken ct)
    {
        // GetActiveAsync() is BurnSettingsInfo? — guard for nullable + TreatWarningsAsErrors.
        var es = await burnSettings.GetActiveAsync(ct)
            ?? throw new InvalidOperationException("BuildRowAsync requires an active event.");

        var flags = ShiftBrowseQueryFlags.IncludeSignups;
        if (isPrivileged)
            flags |= ShiftBrowseQueryFlags.IncludeAdminOnly | ShiftBrowseQueryFlags.IncludeHidden;

        var shifts = await shiftManagement.GetBrowseShiftsAsync(
            new ShiftBrowseQuery(es.Id, null, null, null, flags));
        // Returns null when the shift isn't in the browse set for this caller (e.g. it
        // just filled/closed, or a visibility race). The caller turns that into a benign
        // reload rather than a 500 — never call .First() here.
        var urgent = shifts.FirstOrDefault(u => u.Shift.Id == shiftId);
        if (urgent is null) return null;
        var item = ShiftBrowseMapper.MapToDisplayItem(urgent);

        var (signedShiftIds, statuses) = ResolveActiveStatuses(userSignups);
        return (item, signedShiftIds.Contains(shiftId), statuses.GetValueOrDefault(shiftId));
    }

    private async Task<List<DepartmentShiftGroup>> BuildDepartmentGroupsAsync(
        IReadOnlyList<UrgentShiftInfo> shifts)
    {
        var teamsById = await teamService.GetTeamsAsync();

        return shifts
            .GroupBy(u => u.Rota.TeamId)
            .Select(deptGroup =>
            {
                var firstRota = deptGroup.OrderBy(x => x.Shift.Id).First().Rota;
                var team = teamsById.TryGetValue(firstRota.TeamId, out var t) ? t : null;
                var deptName = team?.Name ?? string.Empty;
                var deptSlug = team?.Slug ?? string.Empty;
                return new DepartmentShiftGroup
                {
                    TeamId = firstRota.TeamId,
                    TeamName = deptName,
                    TeamDescription = team?.Description,
                    TeamSlug = deptSlug,
                    Rotas = deptGroup
                        .GroupBy(u => u.Shift.RotaId)
                        .Select(rg => ShiftBrowseMapper.BuildRotaGroup(rg, deptName, deptSlug))
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

        var depts = await shiftManagement.GetDepartmentsWithRotasAsync(eventSettingsId);
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

    /// <summary>
    /// Orders pies for display: each promoted sub-team is placed immediately
    /// after its parent (using the parent's name as the group key), instead
    /// of strict alphabetical order. Multiple sub-teams under the same parent
    /// sort alphabetically. <c>internal</c> so unit tests can target it.
    /// </summary>
    internal static IReadOnlyList<DepartmentCoveragePie> OrderPiesGroupedByParent(
        IReadOnlyList<DepartmentCoveragePie> pies) =>
        pies
            .OrderBy(p => p.ParentTeamName ?? p.TeamName, StringComparer.Ordinal)
            .ThenBy(p => p.IsSubTeam ? 1 : 0)
            .ThenBy(p => p.TeamName, StringComparer.Ordinal)
            .ToList();
}
