using Humans.Base.Extensions;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Microsoft.Extensions.Localization;

namespace Humans.Shifts.Services;

/// <summary>Reads range candidates; the signup service holds the enrollment lock and owns every write.</summary>
internal sealed class ShiftRangeSelection(
    IShiftManagementRepository repo,
    EventCalendarResolver calendarResolver,
    IStringLocalizer<ShiftsResource> localizer)
{
    internal RangeSignupCandidateSelection PruneDuplicateRangeShifts(
        List<Shift> shiftsInRange,
        IReadOnlyCollection<ShiftSignup> existingSignups,
        EventSettingsInfo eventSettings,
        bool skipConflicts)
    {
        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var activeShiftIds = existingSignups
            .Where(s => shiftIdsInRange.Contains(s.ShiftId))
            .Select(s => s.ShiftId)
            .ToHashSet();

        if (activeShiftIds.Count == 0)
            return new RangeSignupCandidateSelection(shiftsInRange, [], null);

        if (!skipConflicts)
            return new RangeSignupCandidateSelection(
                shiftsInRange,
                [],
                SignupResult.Fail(localizer["Shifts_Signup_RangeDuplicate"]));

        var alreadySignedUpDays = shiftsInRange
            .Where(s => activeShiftIds.Contains(s.Id))
            .Select(s => s.DayOffset)
            .ToList();
        var dayList = FormatRangeDayList(eventSettings, alreadySignedUpDays);
        var warnings = new List<string> { localizer["Shifts_Signup_RangeDuplicateWarning", dayList].Value };
        var remainingShifts = shiftsInRange
            .Where(s => !activeShiftIds.Contains(s.Id))
            .ToList();

        return new RangeSignupCandidateSelection(remainingShifts, warnings, null);
    }

    internal async Task<RangeSignupCandidateSelection> PruneConflictingRangeShiftsAsync(
        List<Shift> shiftsInRange,
        IReadOnlyCollection<ShiftSignup> existingSignups,
        EventSettingsInfo eventSettings,
        IReadOnlyList<string> existingWarnings,
        bool skipConflicts)
    {
        var conflictingDays = await GetConflictingRangeDaysAsync(shiftsInRange, existingSignups, eventSettings);
        if (conflictingDays.Count == 0)
            return new RangeSignupCandidateSelection(shiftsInRange, existingWarnings.ToList(), null);

        var dayList = FormatRangeDayList(eventSettings, conflictingDays);
        if (!skipConflicts)
            return new RangeSignupCandidateSelection(
                shiftsInRange,
                existingWarnings.ToList(),
                SignupResult.Fail(localizer["Shifts_Signup_RangeConflictDays", dayList].Value));

        var warnings = existingWarnings.Append(localizer["Shifts_Signup_RangeConflictDays", dayList].Value).ToList();
        var remainingShifts = shiftsInRange
            .Where(s => !conflictingDays.Contains(s.DayOffset))
            .ToList();

        return new RangeSignupCandidateSelection(remainingShifts, warnings, null);
    }

    internal async Task<List<int>> GetConflictingRangeDaysAsync(
        IReadOnlyList<Shift> shiftsInRange,
        IEnumerable<ShiftSignup> existingSignups,
        EventSettingsInfo eventSettings)
    {
        // Range candidates are all-day shifts from one rota, so two on one date overlap even with no bookings.
        var conflictingDays = shiftsInRange.GroupBy(s => s.DayOffset)
            .Where(day => day.Count() > 1)
            .Select(day => day.Key)
            .ToHashSet();
        var confirmedSignups = existingSignups.Where(s => s.Status == SignupStatus.Confirmed).ToList();
        foreach (var shift in shiftsInRange)
        {
            if (conflictingDays.Contains(shift.DayOffset)) continue;
            var shiftStart = shift.GetAbsoluteStart(eventSettings);
            var shiftEnd = shift.GetAbsoluteEnd(eventSettings);

            foreach (var existing in confirmedSignups)
            {
                var existingCalendar = await calendarResolver.GetAsync(existing.Shift.Rota.EventSettingsId);
                if (existingCalendar is null) continue;
                var existingStart = existing.Shift.GetAbsoluteStart(existingCalendar);
                var existingEnd = existing.Shift.GetAbsoluteEnd(existingCalendar);

                if (shiftStart < existingEnd && shiftEnd > existingStart)
                {
                    conflictingDays.Add(shift.DayOffset);
                    break;
                }
            }
        }

        return shiftsInRange.Select(s => s.DayOffset).Where(conflictingDays.Contains).Distinct().ToList();
    }

    internal async Task<RangeCapacitySelection> SelectCapacityAvailableRangeShiftsAsync(List<Shift> shiftsInRange)
    {
        var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
        var signupCounts = await repo.GetConfirmedSignupCountsByShiftAsync(shiftIdsInRange);
        var fullDays = shiftsInRange
            .Where(s => signupCounts.GetValueOrDefault(s.Id) >= s.MaxVolunteers)
            .Select(s => s.DayOffset)
            .ToList();

        var availableShifts = shiftsInRange
            .Where(s => signupCounts.GetValueOrDefault(s.Id) < s.MaxVolunteers)
            .ToList();

        return new RangeCapacitySelection(availableShifts, fullDays);
    }

    internal static string FormatRangeDayList(EventSettingsInfo eventSettings, IEnumerable<int> dayOffsets)
        => string.Join(", ", dayOffsets.Select(offset => eventSettings.GateOpeningDate.PlusDays(offset).ToWeekdayDayMonth()));

    internal static List<Shift> SelectAllDayRangeShifts(Rota rota, int startDayOffset, int endDayOffset) =>
        rota.Shifts
            .Where(s => s.IsAllDay && s.DayOffset >= startDayOffset && s.DayOffset <= endDayOffset)
            .OrderBy(s => s.DayOffset)
            .ToList();

    internal sealed record RangeSignupCandidateSelection(
        List<Shift> Shifts,
        List<string> Warnings,
        SignupResult? Failure);

    internal sealed record RangeCapacitySelection(
        List<Shift> AvailableShifts,
        List<int> FullDayOffsets);
}
