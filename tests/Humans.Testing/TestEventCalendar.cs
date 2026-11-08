using Humans.Settings.Contracts;

using NodaTime;

namespace Humans.Testing;

/// <summary>
/// Settings' calendar for one event, as a fake <see cref="ISettingsService"/> serves it. Mutable so a test can move
/// a date after seeding and the next read sees it.
/// </summary>
public sealed class TestEventCalendar
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string EventName { get; set; } = string.Empty;

    public int Year { get; set; }

    public string TimeZoneId { get; set; } = string.Empty;

    public LocalDate GateOpeningDate { get; set; }

    public int BuildStartOffset { get; set; }

    public int EventEndOffset { get; set; }

    public int StrikeEndOffset { get; set; }

    public Dictionary<int, int> EarlyEntryCapacity { get; set; } = new();

    public Dictionary<int, int>? BarriosEarlyEntryAllocation { get; set; }

    public Instant? EarlyEntryClose { get; set; }

    /// <summary>Which calendar <see cref="ISettingsService.GetActiveEventSettingsAsync"/> serves.</summary>
    public bool IsActive { get; set; }

    public EventSettingsInfo ToInfo() => new(
        Id: Id,
        EventName: EventName,
        Year: Year,
        TimeZoneId: TimeZoneId,
        GateOpeningDate: GateOpeningDate,
        BuildStartOffset: BuildStartOffset,
        EventEndOffset: EventEndOffset,
        StrikeEndOffset: StrikeEndOffset,
        EarlyEntryCapacity: new Dictionary<int, int>(EarlyEntryCapacity),
        BarriosEarlyEntryAllocation: BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(BarriosEarlyEntryAllocation),
        EarlyEntryClose: EarlyEntryClose);
}
