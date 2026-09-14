using NodaTime;

using Humans.Users.Contracts;

namespace Humans.Users.Domain;

/// <summary>
/// A volunteer history entry documenting a member's involvement in events, roles, or camps.
/// </summary>
internal sealed class VolunteerHistoryEntry
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    public Profile Profile { get; set; } = null!;

    /// <summary>
    /// Date of the event/involvement. Users can enter full date or just use first of month.
    /// Displayed as "Mar'25" format.
    /// </summary>
    public LocalDate Date { get; set; }

    public string EventName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
