using NodaTime;


namespace Humans.Users.Domain;

internal sealed class VolunteerHistoryEntry
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    public Profile Profile { get; set; } = null!;

    /// <summary>Full date, or the first of the month; displayed as "Mar'25".</summary>
    public LocalDate Date { get; set; }

    public string EventName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
