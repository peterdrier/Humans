namespace Humans.Camps.Contracts;

/// <summary>Already scored by this section. Only <see cref="Name"/> is matched.</summary>
/// <remarks>
/// <paramref name="Name"/> is the public-year season name, else the camp's slug. It is only an alphabetical
/// tiebreak and is never rendered.
/// </remarks>
public record CampSearchHit(
    Guid CampId,
    string Name,
    int Score);
