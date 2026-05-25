using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.EarlyEntry;
using NodaTime;

namespace Humans.Application.Services.Shifts;

/// <summary>
/// Pure projection from confirmed build-shift rows to EE grants. Per user, the
/// earliest local shift day drives the grant: entry date = that day - 1, source
/// = "Shift: {team of that earliest shift}". Callers pass only build-period rows.
/// </summary>
internal static class ShiftEarlyEntryProjection
{
    internal static IReadOnlyList<EarlyEntryGrant> Project(
        IReadOnlyList<ConfirmedShiftRow> rows,
        DateTimeZone zone,
        IReadOnlyDictionary<Guid, string> teamNames)
    {
        var earliest = new Dictionary<Guid, (LocalDate day, Guid teamId)>();
        foreach (var r in rows)
        {
            var day = r.StartsAtUtc.InZone(zone).LocalDateTime.Date;
            if (!earliest.TryGetValue(r.UserId, out var cur) || day < cur.day)
                earliest[r.UserId] = (day, r.TeamId);
        }

        var grants = new List<EarlyEntryGrant>(earliest.Count);
        foreach (var (userId, (day, teamId)) in earliest)
        {
            var team = teamNames.GetValueOrDefault(teamId, "shift");
            grants.Add(new EarlyEntryGrant(userId, day.PlusDays(-1), $"Shift: {team}"));
        }
        return grants;
    }

    /// <summary>Earliest local shift day per user. Shared by the XLSX export and the EE provider.</summary>
    internal static Dictionary<Guid, LocalDate> FirstShiftDayByUser(
        IReadOnlyList<ConfirmedShiftRow> rows, DateTimeZone zone)
    {
        var first = new Dictionary<Guid, LocalDate>();
        foreach (var r in rows)
        {
            var day = r.StartsAtUtc.InZone(zone).LocalDateTime.Date;
            if (!first.TryGetValue(r.UserId, out var existing) || day < existing)
                first[r.UserId] = day;
        }
        return first;
    }
}
