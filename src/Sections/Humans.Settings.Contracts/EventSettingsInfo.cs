using NodaTime;

namespace Humans.Settings.Contracts;

/// <summary>
/// The app-wide event settings, as every section reads them: identity, the
/// calendar anchor and build calendar, and the early-entry window and capacity.
/// Section-specific knobs are not here — the shift-browsing switch, the global
/// volunteer cap and the reminder lead time stay on the Shifts-owned row.
/// </summary>
/// <remarks>
/// The underlying entity is Settings-internal and never leaves the section
/// (design-rules §2c, <c>memory/architecture/no-cross-section-ef-joins.md</c>).
/// <see cref="Id"/> is what a section stores when it needs to point at an event
/// cycle — <c>Rota.EventSettingsId</c>, <c>EventGuideSettings.EventSettingsId</c>.
/// <see cref="Status"/> is on the DTO so a save round-trips: reads that go
/// through <c>GetActiveEventSettingsAsync</c> can ignore it.
/// </remarks>
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
    EventSettingsStatus Status = EventSettingsStatus.Active) : IEventSettingsInfo
{
    /// <summary>
    /// Step-function lookup: returns the cumulative EE capacity for the
    /// largest key in <see cref="EarlyEntryCapacity"/> that is ≤
    /// <paramref name="dayOffset"/>, or 0 if none qualifies. Keeps
    /// cross-section callers (camps, art) from reimplementing the lookup.
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
