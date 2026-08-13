using Humans.Shifts.Services.Dtos;
using Humans.Domain.Entities;
using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// Flattens the section's cached <see cref="ShiftUserView"/> row bundle into
/// the <see cref="ShiftUserSummary"/> that crosses the section boundary.
/// </summary>
/// <remarks>
/// The section's singleton decorator answers <see cref="IShiftView"/>
/// through this, off the same cached rows it serves
/// <c>IShiftRowView</c> from, so the boundary shape and the section's own
/// reads can never drift.
///
/// <para>
/// A signup whose <c>Shift</c>, <c>Rota</c> or <c>Rota.EventSettings</c>
/// navigation is missing is dropped rather than projected with placeholder
/// dates: every scalar here is resolved against the event, and the consumers
/// that used to walk these navigations all guarded against the same nulls.
/// </para>
/// </remarks>
internal static class ShiftUserSummaryProjection
{
    /// <summary>Projects one user's row bundle.</summary>
    internal static ShiftUserSummary ToSummary(this ShiftUserView rows) => new(
        rows.UserId,
        [.. rows.TagPreferences.Select(p =>
            new ShiftTagPreferenceInfo(p.ShiftTagId, p.ShiftTag?.Name ?? string.Empty))],
        [.. rows.Signups
            .Where(s => s.Shift?.Rota?.EventSettings is not null)
            .Select(ToSummary)]);

    /// <summary>Projects a keyed batch of row bundles.</summary>
    internal static IReadOnlyDictionary<Guid, ShiftUserSummary> ToSummaries(
        this IReadOnlyDictionary<Guid, ShiftUserView> rows) =>
        rows.ToDictionary(kv => kv.Key, kv => kv.Value.ToSummary());

    private static ShiftSignupSummary ToSummary(ShiftSignup signup)
    {
        var shift = signup.Shift;
        var rota = shift.Rota;
        var settings = rota.EventSettings;

        return new ShiftSignupSummary(
            Id: signup.Id,
            ShiftId: signup.ShiftId,
            SignupBlockId: signup.SignupBlockId,
            Status: signup.Status,
            RotaId: rota.Id,
            RotaName: rota.Name,
            RotaDescription: rota.Description,
            RotaPracticalInfo: rota.PracticalInfo,
            TeamId: rota.TeamId,
            EventSettingsId: rota.EventSettingsId,
            Date: settings.GateOpeningDate.PlusDays(shift.DayOffset),
            Period: shift.GetShiftPeriod(settings),
            IsAllDay: shift.IsAllDay,
            WindowStart: shift.IsAllDay ? Shift.AllDayWindowStart : shift.StartTime,
            WindowEnd: shift.IsAllDay
                ? Shift.AllDayWindowEnd
                : shift.StartTime.PlusTicks(shift.Duration.BclCompatibleTicks),
            DurationHours: shift.IsAllDay ? Shift.AllDayWindowHours : shift.Duration.TotalHours,
            ShiftDescription: shift.Description,
            AbsoluteStart: shift.GetAbsoluteStart(settings),
            AbsoluteEnd: shift.GetAbsoluteEnd(settings));
    }
}
