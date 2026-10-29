using Humans.Shifts.Contracts;
using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>
/// Shifts' knobs for one event cycle. The calendar and early-entry columns are dead: Settings is their source.
/// </summary>
internal sealed class EventSettings : IBurnSettingsInfo
{
    public Guid Id { get; init; }

    public string EventName { get; set; } = string.Empty;

    public int Year { get; set; }

    public string TimeZoneId { get; set; } = string.Empty;

    public LocalDate GateOpeningDate { get; set; }

    public int BuildStartOffset { get; set; }

    public int EventEndOffset { get; set; }

    public int StrikeEndOffset { get; set; }

    public int FirstCrewStartOffset { get; set; } = -25;

    public int SetupWeekStartOffset { get; set; } = -16;

    public int PreEventWeekStartOffset { get; set; } = -9;

    public int FinishingWeekendStartOffset { get; set; } = -4;

    public Dictionary<int, int> EarlyEntryCapacity { get; set; } = new();

    public Dictionary<int, int>? BarriosEarlyEntryAllocation { get; set; }

    public Instant? EarlyEntryClose { get; set; }

    // Shifts' own build sub-period starts (day offsets from gate opening). Null until set here or copied from
    // Settings by the shifts.build-sub-periods data migration; while null, reads fall back to Settings' values.
    // The int offsets above are the old, dead copies.

    public int? SubPeriodFirstCrewStart { get; set; }

    public int? SubPeriodSetupWeekStart { get; set; }

    public int? SubPeriodPreEventWeekStart { get; set; }

    public int? SubPeriodFinishingWeekendStart { get; set; }

    /// <summary>Whether regular volunteers can browse shifts.</summary>
    public bool IsShiftBrowsingOpen { get; set; }

    /// <summary>Optional cap on total volunteer signups per event.</summary>
    public int? GlobalVolunteerCap { get; set; }

    /// <summary>How long before a shift its reminder goes out.</summary>
    public int ReminderLeadTimeHours { get; set; } = 24;

    public bool IsActive { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public ICollection<Rota> Rotas { get; } = new List<Rota>();

    // C# does not surface IBurnSettingsInfo's default implementations through the concrete type, so these forward.

    /// <inheritdoc cref="IBurnSettingsInfo.IsEarlyEntryClosed" />
    public bool IsEarlyEntryClosed(Instant now) =>
        ((IBurnSettingsInfo)this).IsEarlyEntryClosed(now);

    /// <inheritdoc cref="IBurnSettingsInfo.IsEarlyEntrySignupsClosedFor" />
    public bool IsEarlyEntrySignupsClosedFor(bool isPrivileged, Instant now) =>
        ((IBurnSettingsInfo)this).IsEarlyEntrySignupsClosedFor(isPrivileged, now);
}
