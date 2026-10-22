using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Domain;
using Humans.Shifts.Contracts;
using Humans.Settings.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// Projects off the same cached rows as <c>IShiftRowView</c>, so the boundary shape and the section's reads can't
/// drift. A signup missing its Shift, Rota or calendar is dropped, never given placeholder dates.
/// </summary>
internal static class ShiftUserSummaryProjection
{
    internal static ShiftUserSummary ToSummary(this ShiftUserView rows) => new(
        rows.UserId,
        [.. rows.TagPreferences.Select(p =>
            new ShiftTagPreferenceInfo(p.ShiftTagId, p.ShiftTag?.Name ?? string.Empty))],
        rows.Calendar is null
            ? []
            : [.. rows.Signups
                .Where(s => s.Shift?.Rota is not null)
                .Select(s => ToSummary(s, rows.Calendar))]);

    internal static IReadOnlyDictionary<Guid, ShiftUserSummary> ToSummaries(
        this IReadOnlyDictionary<Guid, ShiftUserView> rows) =>
        rows.ToDictionary(kv => kv.Key, kv => kv.Value.ToSummary());

    /// <summary>Needs <c>Shift.Rota</c> loaded.</summary>
    internal static ShiftSignupSummary ToSummary(ShiftSignup signup, EventSettingsInfo settings)
    {
        var shift = signup.Shift;
        var rota = shift.Rota;

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
