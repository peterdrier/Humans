using Humans.Settings.Contracts;
using NodaTime;

namespace Humans.Settings.Domain;

internal sealed class EventSettings : IEventSettingsInfo
{
    public Guid Id { get; init; }

    /// <summary>E.g. "Elsewhere 2026".</summary>
    public string EventName { get; set; } = string.Empty;

    /// <summary>Always <see cref="GateOpeningDate"/>'s year, derived on save; the form has no Year field.</summary>
    public int Year { get; set; }

    /// <summary>A copy of the global <c>Event:TimeZoneId</c> setting, written on save and never read.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    public LocalDate GateOpeningDate { get; set; }

    public int BuildStartOffset { get; set; }

    public int EventEndOffset { get; set; }

    /// <summary>Inclusive.</summary>
    public int StrikeEndOffset { get; set; }

    /// <summary>
    /// Negative offset from which EE-grant holders may enter. Null until configured. <see cref="BuildStartOffset"/> ≤
    /// value &lt; 0.
    /// </summary>
    public int? EarlyEntryStartOffset { get; set; }

    /// <summary>
    /// Admin-set "cycle is wrapped" switch: the member dashboard drops its in-season cards. Set by hand until the
    /// settings revamp derives it. Nullable because a new column on an existing table must be; null reads as false.
    /// </summary>
    public bool? IsOffSeason { get; set; }

    // Build sub-periods: each offset is the inclusive start of its sub-period, ending at the next one's start.
    // BuildStartOffset ≤ FirstCrew < SetupWeek < PreEventWeek < FinishingWeekend < 0, enforced by EventSettingsViewModel.

    public int FirstCrewStartOffset { get; set; } = -25;

    public int SetupWeekStartOffset { get; set; } = -16;

    public int PreEventWeekStartOffset { get; set; } = -9;

    public int FinishingWeekendStartOffset { get; set; } = -4;

    /// <summary>Step function: DayOffset → cumulative EE headcount allowed from that day.</summary>
    public Dictionary<int, int> EarlyEntryCapacity { get; set; } = new();

    /// <summary>DayOffset → reserved barrios headcount, subtracted from the general pool.</summary>
    public Dictionary<int, int>? BarriosEarlyEntryAllocation { get; set; }

    public Instant? EarlyEntryClose { get; set; }

    /// <summary>
    /// No production path removes the row (other sections point at <see cref="Id"/>): a cycle ends by going Inactive,
    /// and nothing sets Deleted today. Only <c>IEventSettingsSeeding.DeleteEventAsync</c> removes rows, for fixtures.
    /// </summary>
    public EventSettingsStatus Status { get; set; } = EventSettingsStatus.Active;

    public Instant CreatedAt { get; set; }

    public Instant UpdatedAt { get; set; }
}
