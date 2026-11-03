
using NodaTime;
using Humans.Settings.Contracts;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The row, not the entity graph: the event-relative fields are resolved inside the section so no consumer fetches
/// the burn, and <see cref="Shift"/> and <see cref="Rota"/> keep the member names the rota partials bind.
/// <see cref="UrgencyScore"/> is 0 when full; <see cref="Signups"/> is empty unless
/// <see cref="ShiftBrowseQueryFlags.IncludeSignups"/> asked.
/// </summary>
public sealed record UrgentShiftInfo(
    ShiftInfo Shift,
    RotaInfo Rota,
    string DepartmentName,
    LocalDate Date,
    ShiftPeriod Period,
    Instant AbsoluteStart,
    Instant AbsoluteEnd,
    double UrgencyScore,
    int ConfirmedCount,
    int RemainingSlots,
    IReadOnlyList<ShiftSignupInfo> Signups);

/// <summary>
/// Member names match <c>Shift</c>'s so the rota partials bind unchanged. <c>StartTime</c> and
/// <c>DurationHours</c> are don't-care when <c>IsAllDay</c>.
/// </summary>
public sealed record ShiftInfo(
    Guid Id,
    Guid RotaId,
    string? Description,
    int DayOffset,
    LocalTime StartTime,
    double DurationHours,
    int MinVolunteers,
    int MaxVolunteers,
    bool AdminOnly,
    bool IsAllDay,
    bool IsEarlyEntry)
{
    internal static readonly LocalTime AllDayWindowStart = new(8, 0);
    internal static readonly LocalTime AllDayWindowEnd = new(18, 0);
    internal static double AllDayWindowHours =>
        Duration.FromTicks(AllDayWindowEnd.TickOfDay - AllDayWindowStart.TickOfDay).TotalHours;

    internal Duration GetDuration() => Duration.FromHours(DurationHours);

    internal Instant GetAbsoluteStart(IEventSettingsInfo eventSettings) =>
        ResolveAbsoluteStart(DayOffset, StartTime, IsAllDay, eventSettings);

    internal Instant GetAbsoluteEnd(IEventSettingsInfo eventSettings) =>
        ResolveAbsoluteEnd(DayOffset, StartTime, GetDuration(), IsAllDay, eventSettings);

    internal ShiftPeriod GetShiftPeriod(IEventSettingsInfo eventSettings) =>
        ResolveShiftPeriod(DayOffset, eventSettings);

    internal static Instant ResolveAbsoluteStart(
        int dayOffset, LocalTime startTime, bool isAllDay, IEventSettingsInfo eventSettings)
    {
        var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
        var date = eventSettings.GateOpeningDate.PlusDays(dayOffset);
        var wallTime = isAllDay ? AllDayWindowStart : startTime;
        return date.At(wallTime).InZoneLeniently(tz).ToInstant();
    }

    internal static Instant ResolveAbsoluteEnd(
        int dayOffset, LocalTime startTime, Duration duration, bool isAllDay, IEventSettingsInfo eventSettings)
    {
        if (isAllDay)
        {
            var tz = DateTimeZoneProviders.Tzdb[eventSettings.TimeZoneId];
            var date = eventSettings.GateOpeningDate.PlusDays(dayOffset);
            return date.At(AllDayWindowEnd).InZoneLeniently(tz).ToInstant();
        }
        return ResolveAbsoluteStart(dayOffset, startTime, isAllDay, eventSettings).Plus(duration);
    }

    internal static ShiftPeriod ResolveShiftPeriod(int dayOffset, IEventSettingsInfo eventSettings) =>
        dayOffset < 0 ? ShiftPeriod.Build :
        dayOffset <= eventSettings.EventEndOffset ? ShiftPeriod.Event :
        ShiftPeriod.Strike;
}

/// <summary>Member names match <c>Rota</c>'s so the rota partials bind unchanged.</summary>
public sealed record RotaInfo(
    Guid Id,
    Guid EventSettingsId,
    Guid TeamId,
    string Name,
    string? Description,
    string? PracticalInfo,
    ShiftPriority Priority,
    SignupPolicy Policy,
    RotaPeriod Period,
    bool IsVisibleToVolunteers,
    IReadOnlyList<ShiftTagSummary> Tags);

public sealed record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status);
