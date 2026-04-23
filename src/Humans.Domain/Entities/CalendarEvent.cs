using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A calendar event belonging to a team, supporting both one-off and recurring occurrences.
/// </summary>
public class CalendarEvent
{
    public Guid Id { get; init; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public string? LocationUrl { get; set; }
    public Guid OwningTeamId { get; set; }

    /// <summary>
    /// Cross-domain navigation to the owning <see cref="Team"/>. Kept so that
    /// the EF configuration can still declare the FK + cascade behavior, but
    /// the Application-layer <c>CalendarService</c> stitches team display
    /// names via <see cref="Team"/> lookups through <c>ITeamService</c>
    /// rather than <c>.Include()</c>-ing this nav (design-rules §6).
    /// </summary>
    [Obsolete("Cross-domain nav — resolve via ITeamService.GetByIdsAsync instead of navigating CalendarEvent.OwningTeam. See design-rules §6c.")]
    public Team OwningTeam { get; set; } = null!;
    public Instant StartUtc { get; set; }
    public Instant? EndUtc { get; set; }
    public bool IsAllDay { get; set; }
    public string? RecurrenceRule { get; set; }
    public string? RecurrenceTimezone { get; set; }
    public Instant? RecurrenceUntilUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }
    public Instant? DeletedAt { get; set; }
    public ICollection<CalendarEventException> Exceptions { get; set; } = new List<CalendarEventException>();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!IsAllDay && EndUtc is null)
            errors.Add("EndUtc is required for timed events.");

        if (EndUtc is { } end && end < StartUtc)
            errors.Add("StartUtc must be on or before EndUtc.");

        var hasRule = !string.IsNullOrWhiteSpace(RecurrenceRule);
        var hasZone = !string.IsNullOrWhiteSpace(RecurrenceTimezone);
        if (hasRule != hasZone)
            errors.Add("RecurrenceRule and RecurrenceTimezone must be set together (both or neither).");

        if (string.IsNullOrWhiteSpace(Title))
            errors.Add("Title is required.");

        return errors;
    }
}
