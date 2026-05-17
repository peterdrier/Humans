using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

/// <summary>
/// Cross-section read DTO over the Shifts-owned <c>event_settings</c> row
/// (colloquially "the burn" — Nowhere 2026, etc.). Exposes only the fields
/// other sections legitimately need (identity, calendar anchor, build
/// calendar, early-entry capacity) — Shifts-internal flags
/// (<c>IsShiftBrowsingOpen</c>, <c>GlobalVolunteerCap</c>,
/// <c>ReminderLeadTimeHours</c>) stay on <see cref="IShiftManagementService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Returned by <see cref="IBurnSettingsService"/>. The underlying entity
/// (<c>EventSettings</c>) is Shifts-internal and never leaves the section
/// (design-rules §2c, <c>memory/architecture/no-cross-section-ef-joins.md</c>).
/// </para>
/// <para>
/// "Burn" is the in-house term for an event cycle. The DB table stays
/// <c>event_settings</c> for backwards compatibility; the cross-section
/// surface uses the colloquial name to disambiguate from the unrelated
/// Events section (camp-event guide / submissions).
/// </para>
/// </remarks>
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
    Instant? EarlyEntryClose)
{
    /// <summary>
    /// Step-function lookup: returns the cumulative EE capacity for the
    /// largest key in <see cref="EarlyEntryCapacity"/> that is ≤
    /// <paramref name="dayOffset"/>, or 0 if none qualifies. Mirrors
    /// <c>EventSettings.GetEarlyEntryCapacityForDay</c> so cross-section
    /// callers (camps, art) don't reimplement the lookup.
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
}
