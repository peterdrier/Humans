namespace Humans.Domain.Entities;

/// <summary>
/// A descriptive label that can be applied to rotas. Shared across all teams.
/// Examples: "Heavy lifting", "Working in the sun", "Feeding and hydrating folks".
/// </summary>
public class ShiftTag
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Display name for the tag.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Navigation property to rotas that have this tag (many-to-many via join table).
    /// </summary>
    public ICollection<Rota> Rotas { get; } = new List<Rota>();

    /// <summary>
    /// Navigation property to volunteers who have selected this as a preference.
    /// </summary>
    public ICollection<VolunteerTagPreference> VolunteerPreferences { get; } = new List<VolunteerTagPreference>();
}
