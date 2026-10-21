using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using NodaTime;

namespace Humans.Shifts.Helpers;

/// <summary>
/// The entity's event-relative resolution (<see cref="Shift.GetAbsoluteStart"/>,
/// <see cref="Shift.GetAbsoluteEnd"/>, <see cref="Shift.GetShiftPeriod"/>) over the
/// flattened <see cref="ShiftInfo"/>, sharing the entity's rules rather than copying them.
/// </summary>
internal static class ShiftInfoExtensions
{
    internal static Duration GetDuration(this ShiftInfo shift) => Duration.FromHours(shift.DurationHours);

    internal static Instant GetAbsoluteStart(this ShiftInfo shift, IEventSettingsInfo eventSettings) =>
        Shift.ResolveAbsoluteStart(shift.DayOffset, shift.StartTime, shift.IsAllDay, eventSettings);

    internal static Instant GetAbsoluteEnd(this ShiftInfo shift, IEventSettingsInfo eventSettings) =>
        Shift.ResolveAbsoluteEnd(shift.DayOffset, shift.StartTime, shift.GetDuration(), shift.IsAllDay, eventSettings);

    internal static ShiftPeriod GetShiftPeriod(this ShiftInfo shift, IEventSettingsInfo eventSettings) =>
        Shift.ResolveShiftPeriod(shift.DayOffset, eventSettings);
}
