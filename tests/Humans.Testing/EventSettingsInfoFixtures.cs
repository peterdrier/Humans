using Humans.Settings.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Builds an <see cref="EventSettingsInfo"/> for tests that stub
/// <c>ISettingsService</c>. Every parameter has a default, so a test that only
/// cares about the year writes <c>EventFixtures.Event(year: 2026)</c>.
/// </summary>
/// <remarks>
/// The twin of <see cref="BurnFixtures"/>, which keeps serving the Shifts tests
/// that still stub <c>IBurnSettingsService</c>. Sections outside Shifts read the
/// event cycle through Settings (peterdrier/Humans#1629), and their DTO has no
/// <c>IsShiftBrowsingOpen</c> — that knob stays on the Shifts-owned row.
/// </remarks>
public static class EventFixtures
{
    public static EventSettingsInfo Event(
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
        EventSettingsStatus status = EventSettingsStatus.Active) =>
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
            Status: status);
}
