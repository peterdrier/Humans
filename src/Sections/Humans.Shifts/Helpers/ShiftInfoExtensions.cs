using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using NodaTime;

namespace Humans.Shifts.Helpers;

/// <summary>The entity's time and period rules over the flattened <see cref="ShiftInfo"/>, shared not copied.</summary>
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
