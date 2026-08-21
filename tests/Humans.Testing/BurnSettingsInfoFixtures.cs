using Humans.Shifts.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Builds a <see cref="BurnSettingsInfo"/> for tests that stub
/// <c>IBurnSettingsService</c>. Every parameter has a default, so a test that
/// only cares about the year writes <c>BurnFixtures.Burn(year: 2026)</c>.
/// </summary>
/// <remarks>
/// Shared here rather than copied per test class because the record is
/// positional with eighteen members: fifteen call sites across six section
/// test projects stub it after the Shifts read boundary moved every
/// cross-section burn read off <c>IShiftManagementService.GetActiveAsync</c>
/// (which returned the <c>EventSettings</c> entity) onto this DTO
/// (nobodies-collective/Humans#866).
/// </remarks>
public static class BurnFixtures
{
    public static BurnSettingsInfo Burn(
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
        bool isShiftBrowsingOpen = false) =>
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
            IsShiftBrowsingOpen: isShiftBrowsingOpen);
}
