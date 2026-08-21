using Humans.Shifts.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Builders for <see cref="UrgentShiftInfo"/> and its two nested records.
/// </summary>
/// <remarks>
/// Same call as <c>BurnFixtures</c> (Shifts lane A finding 7): the boundary row
/// is 11 members over two 11-member nested records, and a test that cares about
/// two of them should not have to spell 33. Every parameter is defaulted, so a
/// call reads as exactly the fields under test.
/// </remarks>
public static class UrgentShiftFixtures
{
    /// <summary>A rota header with sensible defaults.</summary>
    public static RotaInfo Rota(
        Guid? id = null,
        string name = "Test Rota",
        ShiftPriority priority = ShiftPriority.Normal,
        RotaPeriod period = RotaPeriod.Event,
        Guid? teamId = null,
        Guid? eventSettingsId = null,
        string? description = null,
        string? practicalInfo = null,
        SignupPolicy policy = SignupPolicy.Public,
        bool isVisibleToVolunteers = true,
        IReadOnlyList<ShiftTagSummary>? tags = null) =>
        new(
            id ?? Guid.NewGuid(),
            eventSettingsId ?? Guid.NewGuid(),
            teamId ?? Guid.NewGuid(),
            name,
            description,
            practicalInfo,
            priority,
            policy,
            period,
            isVisibleToVolunteers,
            tags ?? []);

    /// <summary>A shift's own fields with sensible defaults.</summary>
    public static ShiftInfo Shift(
        Guid? id = null,
        Guid? rotaId = null,
        int dayOffset = 0,
        LocalTime? startTime = null,
        double durationHours = 4,
        int minVolunteers = 1,
        int maxVolunteers = 3,
        bool adminOnly = false,
        bool isAllDay = false,
        string? description = null) =>
        new(
            id ?? Guid.NewGuid(),
            rotaId ?? Guid.NewGuid(),
            description,
            dayOffset,
            startTime ?? new LocalTime(8, 0),
            durationHours,
            minVolunteers,
            maxVolunteers,
            adminOnly,
            isAllDay,
            IsEarlyEntry: dayOffset < 0);

    /// <summary>
    /// A boundary row. <paramref name="shift"/> and <paramref name="rota"/>
    /// default to <see cref="Shift"/> / <see cref="Rota"/>, and the absolute
    /// window is derived from <paramref name="burn"/> when one is supplied so
    /// the row is self-consistent without the caller doing the arithmetic.
    /// </summary>
    public static UrgentShiftInfo Urgent(
        ShiftInfo? shift = null,
        RotaInfo? rota = null,
        IBurnSettingsInfo? burn = null,
        string departmentName = "Test Department",
        double urgencyScore = 0,
        int confirmedCount = 0,
        int remainingSlots = 0,
        ShiftPeriod? period = null,
        IReadOnlyList<ShiftSignupInfo>? signups = null)
    {
        var s = shift ?? Shift();
        var r = rota ?? Rota(id: s.RotaId);
        var gateOpening = burn?.GateOpeningDate ?? new LocalDate(2026, 3, 1);
        var zone = DateTimeZoneProviders.Tzdb[burn?.TimeZoneId ?? "Europe/Madrid"];
        var date = gateOpening.PlusDays(s.DayOffset);
        var start = date
            .At(s.IsAllDay ? new LocalTime(8, 0) : s.StartTime)
            .InZoneLeniently(zone)
            .ToInstant();
        var end = s.IsAllDay
            ? date.At(new LocalTime(18, 0)).InZoneLeniently(zone).ToInstant()
            : start.Plus(Duration.FromHours(s.DurationHours));

        return new UrgentShiftInfo(
            s,
            r,
            departmentName,
            date,
            period ?? (s.DayOffset < 0
                ? ShiftPeriod.Build
                : s.DayOffset <= (burn?.EventEndOffset ?? 6) ? ShiftPeriod.Event : ShiftPeriod.Strike),
            start,
            end,
            urgencyScore,
            confirmedCount,
            remainingSlots,
            signups ?? []);
    }
}
