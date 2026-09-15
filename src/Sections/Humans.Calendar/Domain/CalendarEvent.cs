using NodaTime;

namespace Humans.Calendar.Domain;

/// <summary>
/// A calendar event belonging to a team, supporting both one-off and recurring occurrences.
/// </summary>
internal sealed class CalendarEvent
{
    public Guid Id { get; init; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public string? LocationUrl { get; set; }
    public Guid OwningTeamId { get; set; }

    public Instant? StartUtc { get; set; }
    public Instant? EndUtc { get; set; }
    public LocalDate? StartDate { get; set; }
    public LocalDate? EndDateExclusive { get; set; }
    public LocalDate? RecurrenceUntilDate { get; set; }
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

        if (IsAllDay)
        {
            if (StartDate is null || EndDateExclusive is null || EndDateExclusive <= StartDate)
                errors.Add("An all-day event requires a non-empty date range.");
            if (StartUtc is not null || EndUtc is not null)
                errors.Add("An all-day event cannot have a time.");
        }
        else if (StartUtc is null || StartDate is not null || EndDateExclusive is not null)
            errors.Add("A timed event requires StartUtc and cannot have all-day dates.");

        if (!IsAllDay && EndUtc is null)
            errors.Add("EndUtc is required for timed events.");

        if (EndUtc is { } end && end < StartUtc)
            errors.Add("StartUtc must be on or before EndUtc.");

        var hasRule = !string.IsNullOrWhiteSpace(RecurrenceRule);
        var hasZone = !string.IsNullOrWhiteSpace(RecurrenceTimezone);
        if (!IsAllDay && hasRule != hasZone)
            errors.Add("RecurrenceRule and RecurrenceTimezone must be set together (both or neither).");

        if (string.IsNullOrWhiteSpace(Title))
            errors.Add("Title is required.");

        return errors;
    }
}
