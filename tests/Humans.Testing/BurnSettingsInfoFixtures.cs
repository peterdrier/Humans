using Humans.Settings.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Builds an <see cref="EventSettingsInfo"/> for tests that stub
/// <c>ISettingsService</c>. Every parameter has a default, so a test that
/// only cares about the year writes <c>BurnFixtures.Burn(year: 2026)</c>.
/// </summary>
/// <remarks>
/// Shared here rather than copied per test class because the record is
/// positional with many members: call sites across several section
/// test projects stub it after cross-section event-calendar reads moved
/// off Shifts' <c>IBurnSettingsService</c> onto Settings' <c>ISettingsService</c>
/// (nobodies-collective/Humans#1104).
/// </remarks>
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
