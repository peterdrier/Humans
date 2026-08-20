namespace Humans.Events.Contracts;

/// <summary>
/// One approved event that matched a global-search query. Returned by
/// <c>IEventServiceRead.SearchAsync</c>, already scored by this section — the
/// orchestrator ranks by <see cref="Score"/> and passes <see cref="EventId"/> to
/// <c>&lt;vc:events-search-result&gt;</c>, which fetches everything it renders
/// (nobodies-collective/Humans#1062). Matches Title or Description: a Title match
/// scores via the shared exact/prefix/contains tiers
/// (<c>StringSearchExtensions.NameMatchScore</c>, 100/80/60); a Description-only
/// match uses the same three tiers halved (50/40/30) so it always ranks below every
/// Title match while still being ordered sensibly against other description hits.
/// </summary>
/// <param name="EventId">The matched event's id; the key the row's view component is invoked with.</param>
/// <param name="Title">Display title. Ordering only — the orchestrator uses it as an
/// alphabetical tiebreak and never renders it.</param>
/// <param name="Score">Match strength — see the tiering rules above.</param>
public record EventSearchHit(
    Guid EventId,
    string Title,
    int Score);
