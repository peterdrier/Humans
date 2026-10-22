using NodaTime;

namespace Humans.Settings.Contracts;

/// <summary>
/// Shifts' knobs (browsing switch, volunteer cap, reminder lead time) are not here: they stay on the Shifts-owned
/// row. <see cref="Id"/> is what a section stores to point at a cycle. <see cref="Status"/> is here so a save
/// round-trips; active-event readers can ignore it. <see cref="IsOffSeason"/> hides the dashboard's in-season cards.
/// </summary>
public sealed record EventSettingsInfo(
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
    EventSettingsStatus Status = EventSettingsStatus.Active,
    int? EarlyEntryStartOffset = null,
    bool IsOffSeason = false) : IEventSettingsInfo
{
    /// <summary>
    /// Step function: the capacity at the largest key ≤ <paramref name="dayOffset"/>, or 0 if none qualifies.
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

    // C# surfaces sealed interface members only through an interface-typed
    // receiver; these forward so DTO-typed call sites need no cast.

    /// <inheritdoc cref="IEventSettingsInfo.IsEarlyEntryClosed" />
    public bool IsEarlyEntryClosed(Instant now) =>
        ((IEventSettingsInfo)this).IsEarlyEntryClosed(now);

    /// <inheritdoc cref="IEventSettingsInfo.IsEarlyEntrySignupsClosedFor" />
    public bool IsEarlyEntrySignupsClosedFor(bool isPrivileged, Instant now) =>
        ((IEventSettingsInfo)this).IsEarlyEntrySignupsClosedFor(isPrivileged, now);
}
