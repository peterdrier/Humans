using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Services.Dtos;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftScheduling(
    IShiftManagementRepository repo,
    EventCalendarResolver calendarResolver,
    CachingShiftViewService viewInvalidator,
    IClock clock) : IApplicationService
{
    internal async Task<ShiftGenerationResult> CreateBuildStrikeShiftsAsync(ConfigureBuildStrikeStaffingInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftGenerationResult.Failure("Rota not found.");

        if (rota.Period == RotaPeriod.Event)
            return ShiftGenerationResult.Failure("Build/strike shift generation is only for Build or Strike rotas.");

        var dailyStaffing = input.Days
            .GroupBy(d => d.DayOffset)
            .ToDictionary(
                g => g.Key,
                g => (Min: g.Last().MinVolunteers, Max: g.Last().MaxVolunteers));

        if (dailyStaffing.Count == 0)
            return ShiftGenerationResult.Failure("At least one staffing day is required.");

        foreach (var (minimum, maximum) in dailyStaffing.Values)
        {
            if (GetShiftInputRefusal(minimum, maximum) is { } refusal)
                return ShiftGenerationResult.Failure(refusal);
        }

        var es = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (es is null)
            return ShiftGenerationResult.Failure("Event calendar not configured.");

        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(rota.Period, es);
        foreach (var dayOffset in dailyStaffing.Keys)
        {
            if (dayOffset < periodStart || dayOffset > periodEnd)
                return ShiftGenerationResult.Failure($"Day offset {dayOffset} is outside the {rota.Period.ToString().ToLowerInvariant()} period ({periodStart} to {periodEnd})");
        }

        // Additive: days that already have shifts are skipped.
        var existingDayOffsets = await repo.GetShiftDayOffsetsForRotaAsync(input.RotaId);
        var existingSet = existingDayOffsets.ToHashSet();

        var now = clock.GetCurrentInstant();
        var toInsert = new List<Shift>();

        foreach (var (dayOffset, staffing) in dailyStaffing.Where(d => !existingSet.Contains(d.Key)).OrderBy(d => d.Key))
        {
            toInsert.Add(new Shift
            {
                Id = Guid.NewGuid(),
                RotaId = input.RotaId,
                IsAllDay = true,
                DayOffset = dayOffset,
                // Ignored for all-day rows; midnight/24h is a neutral sentinel.
                StartTime = LocalTime.Midnight,
                Duration = Duration.FromHours(24),
                MinVolunteers = staffing.Min,
                MaxVolunteers = staffing.Max,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        if (toInsert.Count > 0)
        {
            try
            {
                await repo.AddShiftsAsync(toInsert);
            }
            finally
            {
                viewInvalidator.InvalidateRota(input.RotaId);
            }
        }

        return ShiftGenerationResult.Success($"Created {toInsert.Count} shifts for '{rota.Name}'.", toInsert.Count);
    }

    internal async Task<ShiftGenerationResult> GenerateEventShiftsAsync(GenerateEventShiftsInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftGenerationResult.Failure("Rota not found.");

        if (rota.Period != RotaPeriod.Event)
            return ShiftGenerationResult.Failure("Event shift generation is only for Event-period rotas.");

        var es = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (es is null)
            return ShiftGenerationResult.Failure("Event calendar not configured.");
        if (input.StartDayOffset < 0 ||
            input.EndDayOffset > es.EventEndOffset ||
            input.StartDayOffset > input.EndDayOffset)
            return ShiftGenerationResult.Failure("Shift dates must fall within the event period.");

        if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers) is { } staffingRefusal)
            return ShiftGenerationResult.Failure(staffingRefusal);

        if (input.TimeSlots.Count == 0)
            return ShiftGenerationResult.Failure("At least one time slot is required.");

        foreach (var slot in input.TimeSlots)
        {
            if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers, slot.DurationHours) is { } refusal)
                return ShiftGenerationResult.Failure(refusal);
        }

        var now = clock.GetCurrentInstant();
        var toInsert = new List<Shift>();

        for (var day = input.StartDayOffset; day <= input.EndDayOffset; day++)
        {
            foreach (var slot in input.TimeSlots)
            {
                var shift = new Shift
                {
                    Id = Guid.NewGuid(),
                    RotaId = input.RotaId,
                    IsAllDay = false,
                    DayOffset = day,
                    StartTime = slot.StartTime,
                    Duration = Duration.FromHours(slot.DurationHours),
                    MinVolunteers = input.MinVolunteers,
                    MaxVolunteers = input.MaxVolunteers,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                if (GetShiftScheduleRefusal(shift.DayOffset, shift.StartTime, shift.Duration, shift.IsAllDay, es) is { } refusal)
                    return ShiftGenerationResult.Failure(refusal);
                toInsert.Add(shift);
            }
        }

        if (toInsert.Count > 0)
        {
            try
            {
                await repo.AddShiftsAsync(toInsert);
            }
            finally
            {
                viewInvalidator.InvalidateRota(input.RotaId);
            }
        }

        return ShiftGenerationResult.Success($"Generated {toInsert.Count} shifts for '{rota.Name}'.", toInsert.Count);
    }

    internal async Task<ShiftMutationResult> CreateShiftAsync(CreateShiftInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null || rota.TeamId != input.TeamId)
            return ShiftMutationResult.Failure("Rota not found.");

        var es = await calendarResolver.GetAsync(rota.EventSettingsId);
        if (es is null)
            return ShiftMutationResult.Failure("Event calendar not configured.");
        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(rota.Period, es);
        if (input.DayOffset < periodStart || input.DayOffset > periodEnd)
            return ShiftMutationResult.Failure("Shift date must fall within the rota's period.");

        if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers,
                input.DurationHours, input.IsAllDay) is { } refusal)
            return ShiftMutationResult.Failure(refusal);

        var now = clock.GetCurrentInstant();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = input.RotaId,
            Description = input.Description,
            DayOffset = input.DayOffset,
            StartTime = input.StartTime,
            Duration = Duration.FromHours(input.DurationHours),
            MinVolunteers = input.MinVolunteers,
            MaxVolunteers = input.MaxVolunteers,
            AdminOnly = input.AdminOnly,
            IsAllDay = input.IsAllDay,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (GetShiftScheduleRefusal(shift.DayOffset, shift.StartTime, shift.Duration, shift.IsAllDay, es) is { } scheduleRefusal)
            return ShiftMutationResult.Failure(scheduleRefusal);

        try
        {
            await repo.SaveShiftAsync(shift, EntityMutationMode.Add);
        }
        finally
        {
            viewInvalidator.InvalidateRota(input.RotaId);
        }
        return ShiftMutationResult.Success("Shift created.", shift.Id);
    }

    private static string? GetShiftInputRefusal(
        int minimum, int maximum, double? durationHours = null, bool isAllDay = false)
    {
        if (minimum < 0) return "MinVolunteers cannot be negative.";
        if (maximum < 1) return "MaxVolunteers must be at least one.";
        if (minimum > maximum) return "MinVolunteers cannot exceed MaxVolunteers.";
        if (durationHours is not { } hours) return null;
        if (!double.IsFinite(hours) || (!isAllDay && hours <= 0))
            return isAllDay
                ? "Shift duration must be a finite number of hours."
                : "Shift duration must be a positive finite number of hours.";

        Duration duration;
        try
        {
            duration = Duration.FromHours(hours);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Shift duration is outside the supported range.";
        }

        if (!isAllDay)
        {
            if (duration < Duration.FromSeconds(1))
                return "Shift duration must be at least one second.";
            if (duration != Duration.FromSeconds((long)duration.TotalSeconds))
                return "Shift duration must contain a whole number of seconds.";
        }
        return null;
    }

    private static string? GetShiftScheduleRefusal(
        int dayOffset, LocalTime startTime, Duration duration, bool isAllDay, IEventSettingsInfo settings)
    {
        const string refusal = "Shift start and end must fit within years 0001–9999 in UTC and the event timezone.";
        try
        {
            var zone = DateTimeZoneProviders.Tzdb[settings.TimeZoneId];
            var start = Shift.ResolveAbsoluteStart(dayOffset, startTime, isAllDay, settings);
            var end = Shift.ResolveAbsoluteEnd(dayOffset, startTime, duration, isAllDay, settings);
            return start.InUtc().Year < 1 || start.InZone(zone).Year < 1
                || end.InUtc().Year < 1 || end.InZone(zone).Year < 1
                ? refusal
                : null;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            return refusal;
        }
    }

    private static (int Start, int End) GetRotaDayOffsetBounds(RotaPeriod period, BurnSettingsInfo es) =>
        period switch
        {
            RotaPeriod.Build => (es.BuildStartOffset, -1),
            RotaPeriod.Event => (0, es.EventEndOffset),
            RotaPeriod.Strike => (es.EventEndOffset + 1, es.StrikeEndOffset),
            _ => (es.BuildStartOffset, es.StrikeEndOffset)
        };

    internal async Task<ShiftMutationResult> UpdateShiftAsync(UpdateShiftInput input)
    {
        var shift = await repo.GetShiftAsync(input.ShiftId, ShiftReadShape.Rota);
        if (shift is null || shift.Rota.TeamId != input.TeamId)
            return ShiftMutationResult.Failure("Shift not found.");

        var es = await calendarResolver.GetAsync(shift.Rota.EventSettingsId);
        if (es is null)
            return ShiftMutationResult.Failure("Event calendar not configured.");
        var (periodStart, periodEnd) = GetRotaDayOffsetBounds(shift.Rota.Period, es);
        if (input.DayOffset < periodStart || input.DayOffset > periodEnd)
            return ShiftMutationResult.Failure("Shift date must fall within the rota's period.");

        if (GetShiftInputRefusal(input.MinVolunteers, input.MaxVolunteers,
                input.DurationHours, shift.IsAllDay) is { } refusal)
            return ShiftMutationResult.Failure(refusal);

        var duration = Duration.FromHours(input.DurationHours);
        if (GetShiftScheduleRefusal(input.DayOffset, input.StartTime, duration, shift.IsAllDay, es) is { } scheduleRefusal)
            return ShiftMutationResult.Failure(scheduleRefusal);

        shift.Description = input.Description;
        shift.DayOffset = input.DayOffset;
        shift.StartTime = input.StartTime;
        shift.Duration = duration;
        shift.MinVolunteers = input.MinVolunteers;
        shift.MaxVolunteers = input.MaxVolunteers;
        shift.AdminOnly = input.AdminOnly;
        shift.UpdatedAt = clock.GetCurrentInstant();

        try
        {
            await repo.SaveShiftAsync(shift, EntityMutationMode.Update);
        }
        finally
        {
            viewInvalidator.InvalidateShift(shift.Id);
        }
        return ShiftMutationResult.Success("Shift updated.", shift.Id);
    }

}
