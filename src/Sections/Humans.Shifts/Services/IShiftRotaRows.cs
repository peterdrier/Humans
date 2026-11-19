using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

/// <summary>
/// Every rota with its shifts and signups, across all events. <see cref="CachingShiftService"/> serves it from memory;
/// the keyed inner loads it from the database.
/// </summary>
internal interface IShiftRotaRows
{
    /// <summary>Null <paramref name="rotaIds"/> returns every rota; ids with no rota are left out.</summary>
    Task<IReadOnlyList<ShiftAdminRota>> GetRotaRowsAsync(
        IReadOnlyCollection<Guid>? rotaIds = null, CancellationToken ct = default);
}

internal static class ShiftRotaRowsQueries
{
    /// <summary>One event's shifts from the rows, filtered as <paramref name="query"/> says.</summary>
    internal static async Task<IReadOnlyList<EventShift>> GetEventShiftsAsync(
        this IShiftRotaRows rows, ShiftEventQuery query, CancellationToken ct = default)
    {
        var excludeAdminOnly = query.Flags.HasFlag(ShiftEventQueryFlags.ExcludeAdminOnly);
        var excludeHidden = query.Flags.HasFlag(ShiftEventQueryFlags.ExcludeHiddenRotas);
        return [.. (await rows.GetRotaRowsAsync(ct: ct))
            .Where(r => r.Rota.EventSettingsId == query.EventSettingsId
                && (!excludeHidden || r.Rota.IsVisibleToVolunteers)
                && (query.TeamIds is not { Count: > 0 } || query.TeamIds.Contains(r.Rota.TeamId)))
            .SelectMany(r => r.Shifts
                .Where(s => (!excludeAdminOnly || !s.Shift.AdminOnly)
                    && (query.MinDayOffset is not { } min || s.Shift.DayOffset >= min)
                    && (query.MaxDayOffset is not { } max || s.Shift.DayOffset <= max))
                .Select(s => new EventShift(s.Shift, r.Rota, s.Signups)))];
    }

    /// <summary>Pending signups per rota team; a multi-day block counts once.</summary>
    internal static Dictionary<Guid, int> PendingCountsByTeam(this IEnumerable<EventShift> shifts) =>
        shifts
            .SelectMany(s => s.Signups
                .Where(su => su.Status == SignupStatus.Pending)
                .Select(su => (s.Rota.TeamId, BlockKey: su.SignupBlockId ?? su.Id)))
            .Distinct()
            .GroupBy(x => x.TeamId)
            .ToDictionary(g => g.Key, g => g.Count());
}
