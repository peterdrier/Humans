using Humans.Settings.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>Every parameter is defaulted: <c>BurnFixtures.Burn(year: 2026)</c>.</summary>
public static class BurnFixtures
{
    public static EventSettingsInfo Burn(
        Guid? id = null,
        string eventName = "Test Burn",
        int year = 2026,
        string timeZoneId = "Europe/Madrid",
        LocalDate? gateOpeningDate = null,
        int buildStartOffset = -14,
        int eventEndOffset = 6,
        int strikeEndOffset = 9,
        int firstCrewStartOffset = -14,
        int setupWeekStartOffset = -7,
        int preEventWeekStartOffset = -3,
        int finishingWeekendStartOffset = -2,
        IReadOnlyDictionary<int, int>? earlyEntryCapacity = null,
        IReadOnlyDictionary<int, int>? barriosEarlyEntryAllocation = null,
        Instant? earlyEntryClose = null,
        int? earlyEntryStartOffset = null) =>
        new(
            Id: id ?? Guid.NewGuid(),
            EventName: eventName,
            Year: year,
            TimeZoneId: timeZoneId,
            GateOpeningDate: gateOpeningDate ?? new LocalDate(year, 7, 1),
            BuildStartOffset: buildStartOffset,
            EventEndOffset: eventEndOffset,
            StrikeEndOffset: strikeEndOffset,
            FirstCrewStartOffset: firstCrewStartOffset,
            SetupWeekStartOffset: setupWeekStartOffset,
            PreEventWeekStartOffset: preEventWeekStartOffset,
            FinishingWeekendStartOffset: finishingWeekendStartOffset,
            EarlyEntryCapacity: earlyEntryCapacity ?? new Dictionary<int, int>(),
            BarriosEarlyEntryAllocation: barriosEarlyEntryAllocation,
            EarlyEntryClose: earlyEntryClose,
            EarlyEntryStartOffset: earlyEntryStartOffset);
}
