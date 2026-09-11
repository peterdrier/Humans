namespace Humans.Shifts.Domain;

/// <summary>
/// A descriptive label that can be applied to rotas. Shared across all teams.
/// Examples: "Heavy lifting", "Working in the sun", "Feeding and hydrating folks".
/// </summary>
internal sealed class ShiftTag
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public ICollection<Rota> Rotas { get; } = new List<Rota>();

    public ICollection<VolunteerTagPreference> VolunteerPreferences { get; } = new List<VolunteerTagPreference>();
}
