namespace Humans.Teams.Contracts;

/// <summary>Already scored by this section; the search orchestrator ranks by <see cref="Score"/>.</summary>
/// <remarks>
/// Only <paramref name="Name"/> is matched. It is an alphabetical tiebreak, never rendered: the row's view component
/// fetches what it shows from <paramref name="TeamId"/>.
/// </remarks>
public record TeamSearchHit(
    Guid TeamId,
    string Name,
    int Score);
