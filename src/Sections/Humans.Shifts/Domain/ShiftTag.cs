namespace Humans.Shifts.Domain;

/// <summary>Shared across all teams, e.g. "Heavy lifting", "Working in the sun".</summary>
internal sealed class ShiftTag
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public ICollection<Rota> Rotas { get; } = new List<Rota>();

    public ICollection<VolunteerTagPreference> VolunteerPreferences { get; } = new List<VolunteerTagPreference>();
}
