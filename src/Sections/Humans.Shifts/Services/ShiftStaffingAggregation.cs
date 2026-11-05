using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using Humans.Shifts.Models;
using Humans.Teams.Contracts;

namespace Humans.Shifts.Services;

internal static class ShiftStaffingAggregation
{
    internal static Guid DepartmentIdOf(Guid teamId, IReadOnlyDictionary<Guid, TeamInfo> teams) =>
        teams.TryGetValue(teamId, out var team) ? team.ParentTeamId ?? team.Id : teamId;

    internal static List<DepartmentStaffingRow> BuildDepartmentRows(
        IReadOnlyList<Shift> shifts,
        Dictionary<Guid, int> confirmedCounts,
        BurnSettingsInfo es,
        IReadOnlyDictionary<Guid, TeamInfo> teamLookup)
    {
        string NameOf(Guid teamId) =>
            teamLookup.TryGetValue(teamId, out var t) ? t.Name : string.Empty;

        string? SlugOf(Guid teamId) =>
            teamLookup.TryGetValue(teamId, out var t) ? t.Slug : null;

        var groups = shifts
            .GroupBy(s => DepartmentIdOf(s.Rota.TeamId, teamLookup))
            .ToList();

        var rows = new List<DepartmentStaffingRow>();
        foreach (var g in groups)
        {
            var deptShifts = g.ToList();
            var deptId = g.Key;
            var deptName = NameOf(deptId);
            var agg = AggregateShifts(deptShifts, confirmedCounts, es);

            var subgroups = new List<SubgroupStaffingRow>();
            var anySubteam = deptShifts.Any(s =>
                teamLookup.TryGetValue(s.Rota.TeamId, out var t) && t.ParentTeamId != null);
            if (anySubteam)
            {
                var subteamGroups = deptShifts
                    .Where(s => teamLookup.TryGetValue(s.Rota.TeamId, out var t) && t.ParentTeamId != null)
                    .GroupBy(s => s.Rota.TeamId);
                foreach (var sg in subteamGroups)
                {
                    var sTeamId = sg.Key;
                    var sAgg = AggregateShifts(sg.ToList(), confirmedCounts, es);
                    subgroups.Add(new SubgroupStaffingRow(
                        sTeamId, NameOf(sTeamId), SlugOf(sTeamId), IsDirect: false,
                        sAgg.Total, sAgg.Filled, sAgg.TotalSlots, sAgg.FilledSlots, sAgg.Remaining,
                        sAgg.Build, sAgg.Event, sAgg.Strike));
                }

                var directShifts = deptShifts.Where(s => s.Rota.TeamId == deptId).ToList();
                if (directShifts.Count > 0)
                {
                    var dAgg = AggregateShifts(directShifts, confirmedCounts, es);
                    subgroups.Insert(0, new SubgroupStaffingRow(
                        deptId, "Direct", SlugOf(deptId), IsDirect: true,
                        dAgg.Total, dAgg.Filled, dAgg.TotalSlots, dAgg.FilledSlots, dAgg.Remaining,
                        dAgg.Build, dAgg.Event, dAgg.Strike));
                }
            }

            rows.Add(new DepartmentStaffingRow(
                deptId, deptName, SlugOf(deptId),
                agg.Total, agg.Filled, agg.TotalSlots, agg.FilledSlots, agg.Remaining,
                agg.Build, agg.Event, agg.Strike,
                subgroups));
        }

        return rows;
    }

    internal static (int Total, int Filled, int TotalSlots, int FilledSlots, int Remaining, PeriodStaffing Build, PeriodStaffing Event, PeriodStaffing Strike)
        AggregateShifts(IReadOnlyList<Shift> shifts, Dictionary<Guid, int> confirmedCounts, BurnSettingsInfo es)
    {
        int total = shifts.Count;
        int filled = 0;
        int totalSlots = 0;
        int filledSlots = 0;
        int remaining = 0;
        var periodAgg = new Dictionary<ShiftPeriod, (int Total, int Filled, int TotalSlots, int FilledSlots, int Remaining)>
        {
            [ShiftPeriod.Build] = (0, 0, 0, 0, 0),
            [ShiftPeriod.Event] = (0, 0, 0, 0, 0),
            [ShiftPeriod.Strike] = (0, 0, 0, 0, 0),
        };

        foreach (var s in shifts)
        {
            var confirmed = confirmedCounts.TryGetValue(s.Id, out var c) ? c : 0;
            var isFilled = confirmed >= s.MinVolunteers;
            var filledSlotsHere = Math.Min(confirmed, s.MaxVolunteers);
            var slotsLeft = Math.Max(0, s.MaxVolunteers - confirmed);

            if (isFilled) filled++;
            totalSlots = checked(totalSlots + s.MaxVolunteers);
            filledSlots = checked(filledSlots + filledSlotsHere);
            remaining += slotsLeft;

            var p = s.GetShiftPeriod(es);
            var cur = periodAgg[p];
            periodAgg[p] = (
                cur.Total + 1,
                cur.Filled + (isFilled ? 1 : 0),
                cur.TotalSlots + s.MaxVolunteers,
                cur.FilledSlots + filledSlotsHere,
                cur.Remaining + slotsLeft);
        }

        PeriodStaffing ToStaffing(ShiftPeriod p)
        {
            var v = periodAgg[p];
            return new PeriodStaffing(v.Total, v.Filled, v.TotalSlots, v.FilledSlots, v.Remaining);
        }

        return (total, filled, totalSlots, filledSlots, remaining, ToStaffing(ShiftPeriod.Build), ToStaffing(ShiftPeriod.Event), ToStaffing(ShiftPeriod.Strike));
    }

    internal static IReadOnlyDictionary<Guid, TeamInfo> IncludeParents(
        IReadOnlyDictionary<Guid, TeamInfo> teamsById,
        IEnumerable<Guid> teamIds)
    {
        var result = new Dictionary<Guid, TeamInfo>();
        var pending = new Queue<Guid>(teamIds.Distinct());
        while (pending.TryDequeue(out var teamId))
        {
            if (result.ContainsKey(teamId) || !teamsById.TryGetValue(teamId, out var team))
                continue;

            result[teamId] = team;
            if (team.ParentTeamId is { } parentId)
                pending.Enqueue(parentId);
        }

        return result;
    }
}
