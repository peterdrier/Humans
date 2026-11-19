using NodaTime;


namespace Humans.Users.Domain;

internal sealed class VolunteerHistoryEntry
{
    public Guid Id { get; init; }

    public Guid ProfileId { get; init; }

    /// <summary>
    /// The owning user: the key this table is moving to. Null only on rows written before the column existed, until
    /// the <c>users.profile-children-user-id</c> data migration fills them.
    /// </summary>
    public Guid? UserId { get; set; }

    public Profile Profile { get; set; } = null!;

    /// <summary>Full date, or the first of the month; displayed as "Mar'25".</summary>
    public LocalDate Date { get; set; }

    public string EventName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }
}
