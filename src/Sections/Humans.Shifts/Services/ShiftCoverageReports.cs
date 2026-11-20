using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Helpers;
using Humans.Shifts.Models;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftCoverageReports(
    EventCalendarResolver calendarResolver,
    IServiceProvider serviceProvider) : IApplicationService
{
    private static readonly decimal AllDayShiftHours = (decimal)Shift.AllDayWindowHours;
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();
    private IShiftRotaRows RotaRows => serviceProvider.GetRequiredService<IShiftRotaRows>();

    public async Task<ShiftStaffingSnapshot> GetStaffingSnapshotAsync(
        Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null,
        BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return ShiftStaffingSnapshot.Empty;

        var tz = es.TimeZone;

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return ShiftStaffingSnapshot.Empty;

        var departmentTeamIds = departmentId.HasValue
            ? ShiftStaffingAggregation.ResolveDepartmentTeamIds(departmentId.Value, await TeamService.GetTeamsAsync(ct))
            : null;
        var shifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            departmentTeamIds), ct);

        var staffingData = new List<DailyStaffingData>();
        var staffingHours = new List<DailyStaffingHours>();

        foreach (var dayOffset in dayOffsets)
        {
            var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
            var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
            var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
            var dateLabel = dayDate.ToWeekdayDayMonth();

            var overlapping = shifts.Where(s => OverlapsDay(s, es, dayStart, dayEnd)).ToList();

            var periodLabel = dayOffset < 0 ? "Set-up" : dayOffset <= es.EventEndOffset ? "Event" : "Strike";
            var totalSlots = overlapping.Sum(s => s.Shift.MaxVolunteers);
            var minSlots = overlapping.Sum(s => s.Shift.MinVolunteers);
            var confirmedCount = overlapping.Sum(s => s.ConfirmedCount);

            var essentialHours = 0.0;
            var importantHours = 0.0;
            var normalHours = 0.0;

            foreach (var shift in overlapping)
            {
                var overlapStart = Instant.Max(dayStart, shift.Shift.GetAbsoluteStart(es));
                var overlapEnd = Instant.Min(dayEnd, shift.Shift.GetAbsoluteEnd(es));
                var totalHours = (overlapEnd - overlapStart).TotalHours * shift.Shift.MaxVolunteers;

                switch (shift.Rota.Priority)
                {
                    case ShiftPriority.Essential:
                        essentialHours += totalHours;
                        break;
                    case ShiftPriority.Important:
                        importantHours += totalHours;
                        break;
                    default:
                        normalHours += totalHours;
                        break;
                }
            }

            staffingData.Add(new DailyStaffingData(dayOffset, dateLabel, confirmedCount, totalSlots, minSlots, periodLabel));
            staffingHours.Add(new DailyStaffingHours(dayOffset, dateLabel, essentialHours, importantHours, normalHours));
        }

        return new ShiftStaffingSnapshot(staffingData, staffingHours);
    }

    public async Task<IReadOnlyList<DepartmentCoveragePie>> GetDepartmentCoveragePiesAsync(
        Guid eventSettingsId,
        LocalDate? fromDate = null,
        LocalDate? toDate = null,
        CancellationToken ct = default)
    {
        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var allRotas = (await RotaRows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == eventSettingsId)
            .ToList();
        var teamIdsWithRotas = allRotas.Select(r => r.Rota.TeamId).Distinct().ToList();
        if (teamIdsWithRotas.Count == 0) return [];

        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIdsWithRotas);

        var pieTeams = teamLookup.Values.Where(IsInDirectory).ToList();
        var pieTeamIds = pieTeams.Select(t => t.Id).ToHashSet();

        var requested = new Dictionary<Guid, decimal>();
        var filled = new Dictionary<Guid, decimal>();

        foreach (var row in allRotas.Where(r => r.Rota.IsVisibleToVolunteers))
        {
            if (!teamLookup.TryGetValue(row.Rota.TeamId, out var rotaTeam)) continue;

            Guid? bucketId = pieTeamIds.Contains(rotaTeam.Id)
                ? rotaTeam.Id
                : (rotaTeam.ParentTeamId is { } pid && pieTeamIds.Contains(pid) ? pid : null);
            if (bucketId is null) continue;

            foreach (var (shift, confirmed) in row.Shifts.Where(s => !s.Shift.AdminOnly).Select(s => (s.Shift, s.ConfirmedCount)))
            {
                if (fromDate is not null || toDate is not null)
                {
                    var shiftDate = es.GateOpeningDate.PlusDays(shift.DayOffset);
                    if (fromDate is { } from && shiftDate < from) continue;
                    if (toDate is { } to && shiftDate > to) continue;
                }

                var hours = shift.IsAllDay ? AllDayShiftHours : (decimal)shift.DurationHours;
                requested[bucketId.Value] = requested.GetValueOrDefault(bucketId.Value)
                    + hours * shift.MaxVolunteers;

                var capped = Math.Min(confirmed, shift.MaxVolunteers);
                filled[bucketId.Value] = filled.GetValueOrDefault(bucketId.Value) + hours * capped;
            }
        }

        // Display order (sub-team beside parent) lives in the view model.
        return pieTeams
            .Where(t => requested.GetValueOrDefault(t.Id) > 0)
            .Select(t => new DepartmentCoveragePie(
                TeamId: t.Id,
                TeamName: t.Name,
                TeamSlug: t.Slug,
                IsSubTeam: t.ParentTeamId is not null,
                ParentTeamId: t.ParentTeamId,
                ParentTeamName: t.ParentTeamId is { } pid && teamLookup.TryGetValue(pid, out var parent)
                    ? parent.Name
                    : null,
                RequestedHours: requested[t.Id],
                FilledHours: filled.GetValueOrDefault(t.Id)))
            .ToList();
    }

    public async Task<IReadOnlyList<DailyDepartmentStaffing>> GetDailyDepartmentStaffingAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        // Event days have different day-over-day dynamics.
        if (period is not (ShiftPeriod.Build or ShiftPeriod.Strike))
            return [];

        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var tz = es.TimeZone;

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return [];

        var shifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);

        var teamIdsOnRotas = shifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIdsOnRotas);

        (Guid Id, string Name) DeptOf(EventShift s)
        {
            if (!teamLookup.TryGetValue(s.Rota.TeamId, out var t))
                return (s.Rota.TeamId, string.Empty);
            var parentId = t.ParentTeamId ?? t.Id;
            var parentName = t.ParentTeamId is null
                ? t.Name
                : teamLookup.TryGetValue(parentId, out var parent) ? parent.Name : t.Name;
            return (parentId, parentName);
        }

        var results = new List<DailyDepartmentStaffing>(dayOffsets.Count);
        foreach (var dayOffset in dayOffsets)
        {
            var dayDate = es.GateOpeningDate.PlusDays(dayOffset);
            var dayStart = dayDate.AtStartOfDayInZone(tz).ToInstant();
            var dayEnd = dayDate.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();
            var dateLabel = dayDate.ToWeekdayDayMonth();

            var overlapping = shifts.Where(s => OverlapsDay(s, es, dayStart, dayEnd));

            var byDept = new Dictionary<Guid, (string Name, int Count)>();
            foreach (var s in overlapping)
            {
                var (deptId, deptName) = DeptOf(s);
                var confirmed = s.ConfirmedCount;
                if (confirmed == 0) continue;
                var cur = byDept.TryGetValue(deptId, out var existing) ? existing : (Name: deptName, Count: 0);
                byDept[deptId] = (cur.Name, cur.Count + confirmed);
            }

            var depts = byDept.Values
                .Select(v => new DepartmentDayCount(v.Name, v.Count))
                .ToList();

            results.Add(new DailyDepartmentStaffing(dayDate, dateLabel, depts));
        }

        return results;
    }

    public async Task<CoverageHeatmap> GetCoverageHeatmapAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        var empty = new CoverageHeatmap(
            [],
            []);

        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return empty;

        var tz = es.TimeZone;

        var dayOffsets = BuildDayOffsetList(period, subPeriod, es);
        if (dayOffsets.Count == 0) return empty;

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);
        if (allShifts.Count == 0) return empty;

        var teamIds = allShifts.Select(s => s.Rota.TeamId).Distinct().ToList();
        var teamLookup = ShiftStaffingAggregation.IncludeParents(await TeamService.GetTeamsAsync(ct), teamIds);

        var days = dayOffsets
            .Select(off =>
            {
                var date = es.GateOpeningDate.PlusDays(off);
                var label = date.ToWeekdayDayMonth();
                var dayPeriod = off < 0 ? ShiftPeriod.Build : off <= es.EventEndOffset ? ShiftPeriod.Event : ShiftPeriod.Strike;
                return new CoverageHeatmapDay(off, date, label, dayPeriod);
            })
            .ToList();

        // A non-promoted subteam displays as its parent.
        TeamInfo? DisplayTeamFor(EventShift s)
        {
            if (!teamLookup.TryGetValue(s.Rota.TeamId, out var team)) return null;
            if (team.ParentTeamId is null) return team;
            if (team.IsPromotedToDirectory) return team;
            return team.ParentTeamId is Guid pid && teamLookup.TryGetValue(pid, out var parent) ? parent : null;
        }

        var shiftsByDisplayTeam = allShifts
            .Select(s => (Shift: s, Team: DisplayTeamFor(s)))
            .Where(x => x.Team is not null && IsInDirectory(x.Team))
            .GroupBy(x => x.Team!.Id, x => x.Shift)
            .ToList();

        var rows = new List<CoverageHeatmapRotaRow>();
        foreach (var teamGroup in shiftsByDisplayTeam)
        {
            var teamId = teamGroup.Key;
            if (!teamLookup.TryGetValue(teamId, out var team)) continue;

            var shifts = teamGroup.ToList();

            var cells = new List<CoverageHeatmapCell>(days.Count);
            var teamHasAnyShift = false;

            foreach (var day in days)
            {
                var dayStart = day.Date.AtStartOfDayInZone(tz).ToInstant();
                var dayEnd = day.Date.PlusDays(1).AtStartOfDayInZone(tz).ToInstant();

                var overlapping = shifts.Where(s => OverlapsDay(s, es, dayStart, dayEnd)).ToList();

                var totalSlots = overlapping.Sum(s => s.Shift.MaxVolunteers);
                var filledSlots = overlapping.Sum(s => Math.Min(s.ConfirmedCount, s.Shift.MaxVolunteers));

                if (totalSlots > 0) teamHasAnyShift = true;
                cells.Add(new CoverageHeatmapCell(day.DayOffset, totalSlots, filledSlots));
            }

            if (!teamHasAnyShift) continue;

            var departmentName = team.ParentTeamId is Guid pid && teamLookup.TryGetValue(pid, out var parentTeam)
                ? parentTeam.Name
                : team.Name;

            rows.Add(new CoverageHeatmapRotaRow(
                team.Id,
                team.Name,
                departmentName,
                cells));
        }

        return new CoverageHeatmap(days, rows);
    }

    internal async Task<(int Filled, int Total, double Ratio)> ComputeOverallCoverageAsync(CancellationToken ct)
    {
        var es = await calendarResolver.GetActiveAsync(ct);
        if (es is null) return (0, 0, 0d);

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            es.Id,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);
        if (allShifts.Count == 0) return (0, 0, 0d);

        var total = allShifts.Sum(s => s.Shift.MaxVolunteers);
        var filled = allShifts.Sum(s => Math.Min(s.ConfirmedCount, s.Shift.MaxVolunteers));

        var ratio = total == 0 ? 0d : (double)filled / total;
        return (filled, total, ratio);
    }

    public async Task<IReadOnlyList<ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(
        Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null, CancellationToken ct = default)
    {
        if (period is null) return [];

        var es = await calendarResolver.GetAsync(eventSettingsId, ct);
        if (es is null) return [];

        var allShifts = await RotaRows.GetEventShiftsAsync(new ShiftEventQuery(
            eventSettingsId,
            Flags: ShiftEventQueryFlags.ExcludeAdminOnly | ShiftEventQueryFlags.ExcludeHiddenRotas), ct);
        var periodShifts = allShifts.Where(s => s.Shift.GetShiftPeriod(es) == period.Value).ToList();

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            periodShifts = periodShifts.Where(s => s.Shift.DayOffset >= start && s.Shift.DayOffset < end).ToList();
        }

        // All-day shifts collapse into one bucket; others group by whole-hour duration.
        var grouped = periodShifts
            .GroupBy(s => (
                s.Shift.IsAllDay,
                Hours: s.Shift.IsAllDay ? 0 : (int)s.Shift.DurationHours))
            .Select(g => new ShiftDurationBreakdownRow(
                IsAllDay: g.Key.IsAllDay,
                DurationHours: g.Key.Hours,
                TotalSlots: g.Sum(s => s.Shift.MaxVolunteers),
                FilledSlots: g.Sum(s => Math.Min(s.ConfirmedCount, s.Shift.MaxVolunteers))))
            .ToList();

        return grouped;
    }

    /// <summary>All three periods when <paramref name="period"/> is null.</summary>
    private static List<int> BuildDayOffsetList(
        ShiftPeriod? period, BuildSubPeriod? subPeriod, BurnSettingsInfo es)
    {
        var offsets = new List<int>();
        if (period is null or ShiftPeriod.Build)
            for (var d = es.BuildStartOffset; d < 0; d++) offsets.Add(d);
        if (period is null or ShiftPeriod.Event)
            for (var d = 0; d <= es.EventEndOffset; d++) offsets.Add(d);
        if (period is null or ShiftPeriod.Strike)
            for (var d = es.EventEndOffset + 1; d <= es.StrikeEndOffset; d++) offsets.Add(d);

        if (period == ShiftPeriod.Build && subPeriod is not null)
        {
            var (start, end) = BuildSubPeriodClassifier.BoundsFor(subPeriod.Value, es);
            offsets = offsets.Where(d => d >= start && d < end).ToList();
        }

        return offsets;
    }

    private static bool OverlapsDay(EventShift shift, BurnSettingsInfo calendar, Instant dayStart, Instant dayEnd)
    {
        var start = shift.Shift.GetAbsoluteStart(calendar);
        var end = shift.Shift.GetAbsoluteEnd(calendar);
        return start < dayEnd && end > dayStart;
    }

    private static bool IsInDirectory(TeamInfo team) =>
        team.ParentTeamId is null || team.IsPromotedToDirectory;
}
