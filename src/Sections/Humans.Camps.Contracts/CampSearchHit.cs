namespace Humans.Camps.Contracts;

/// <summary>
/// One camp that matched a global-search query. Returned by
/// <c>ICampService.SearchAsync</c> with the public-year season's display
/// name already resolved so the orchestrator never has to traverse
/// <c>Camp.Seasons</c> to render the row. Names-only matching: only
/// <see cref="Name"/> is matched.
/// </summary>
/// <param name="Slug">URL slug; used for the detail URL and as subtitle.</param>
/// <param name="Name">The public-year season name (the only matched field),
/// falling back to the camp's slug when no season exists for that year.</param>
public record CampSearchHit(
    string Slug,
    string Name);
