
using NodaTime;
using Humans.Settings.Contracts;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The calendar from Settings plus the Shifts browsing switch. "Burn" names an event cycle, to keep clear of the
/// unrelated Events section. Shifts' tuning knobs stay internal.
/// </summary>
public sealed record BurnSettingsInfo(
    Guid Id,
    string EventName,
    int Year,
    string TimeZoneId,
    LocalDate GateOpeningDate,
    int BuildStartOffset,
    int EventEndOffset,
    int StrikeEndOffset,
    int FirstCrewStartOffset,
    int SetupWeekStartOffset,
    int PreEventWeekStartOffset,
    int FinishingWeekendStartOffset,
    IReadOnlyDictionary<int, int> EarlyEntryCapacity,
    IReadOnlyDictionary<int, int>? BarriosEarlyEntryAllocation,
    Instant? EarlyEntryClose,
    bool IsShiftBrowsingOpen) : IBurnSettingsInfo, IEventSettingsInfo
{
    /// <summary>
    /// Capacity at the largest key at or below <paramref name="dayOffset"/>, else 0. Mirrors
    /// <see cref="EventSettingsInfo.GetEarlyEntryCapacityForDay"/>.
    /// </summary>
    public int GetEarlyEntryCapacityForDay(int dayOffset)
    {
        if (EarlyEntryCapacity.Count == 0)
            return 0;

        var applicableKey = int.MinValue;
        foreach (var key in EarlyEntryCapacity.Keys)
        {
            if (key <= dayOffset && key > applicableKey)
                applicableKey = key;
        }

        return applicableKey == int.MinValue ? 0 : EarlyEntryCapacity[applicableKey];
    }

    // C# surfaces the interface's sealed bodies only through an interface-typed receiver; these spare every call a
    // cast.
    /// <inheritdoc cref="IBurnSettingsInfo.IsEarlyEntryClosed" />
    public bool IsEarlyEntryClosed(Instant now) =>
        ((IBurnSettingsInfo)this).IsEarlyEntryClosed(now);

    /// <inheritdoc cref="IBurnSettingsInfo.IsEarlyEntrySignupsClosedFor" />
    public bool IsEarlyEntrySignupsClosedFor(bool isPrivileged, Instant now) =>
        ((IBurnSettingsInfo)this).IsEarlyEntrySignupsClosedFor(isPrivileged, now);
}
