using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Search;

/// <summary>
/// Top-level search orchestrator for the global <c>/Search</c> page. Fans
/// out to per-section read interfaces (<c>IUserServiceRead</c>,
/// <c>ITeamServiceRead</c>, <c>ICampServiceRead</c>,
/// <c>IShiftManagementService</c>, <c>IEventServiceRead</c>), each of which
/// resolves its own case-insensitive match — from a cached snapshot for
/// teams/camps/humans/events, via Postgres ILike at the DB layer for rotas
/// only. The orchestrator scores and ranks within each type and returns
/// five independently-ranked buckets — there is no cross-modal / relational
/// expansion (see <c>docs/features/global/global-search.md</c>).
///
/// <para>
/// Per design-rules §6, this service NEVER queries another section's
/// tables directly — it only calls the public service interface for each
/// section.
/// </para>
///
/// <para>
/// <b>Search is not an authorization boundary.</b> A hit says a URL exists;
/// it does not say the caller may open it. Enforcement lives at the
/// destination, in whatever shape that destination has: a detail page
/// refuses outright — <c>/Teams/{slug}</c> 404s a hidden team — while a
/// listing renders and omits the row, since a rota hit links to
/// <c>/Shifts?departmentId={teamId}</c>, whose default browse query drops
/// rotas hidden from volunteers. Either way the viewer does not get the
/// thing. Two destinations fall short. <c>/Camps/{slug}</c> has no
/// season-status gate, so a non-public season still renders
/// (nobodies-collective/Humans#993). And a rota from a <i>past</i> event
/// resolves by id but <c>/Shifts</c> only ever builds the active event, so
/// that link cannot reach it even when it is volunteer-visible
/// (nobodies-collective/Humans#998) — a reach problem, not a privacy one.
/// </para>
///
/// <para>
/// Text queries match the public surface only: hidden teams, non-public camp
/// seasons and admin-only rotas never match by name, and humans are matched
/// with <c>PersonSearchFields.PublicAll</c>, so admin-only profile fields
/// match for nobody regardless of role. A <b>GUID</b> query is deliberately
/// different — the Team, Camp and Rota buckets resolve it straight to the
/// entity with no visibility filter. That is a routing convenience for
/// someone who already holds the id, not an authorization statement. Humans
/// are the exception: the id path skips only the field mask, not the
/// eligibility gate, so a user with no profile or a rejected one resolves
/// to nothing by id exactly as by name
/// (<c>CachingUserService.SearchUsersAsync</c>).
/// </para>
/// </summary>
public interface ISearchService : IOrchestrator
{
    /// <summary>
    /// Run a global search. Empty/whitespace <paramref name="query"/>, or
    /// shorter than 2 characters after trim, returns an empty
    /// <see cref="GlobalSearchResults"/>.
    /// </summary>
    /// <param name="query">User-entered text. Trimmed and matched
    /// case-insensitively per <c>memory/feedback_ef_ilike_not_toupper.md</c>.</param>
    /// <param name="onlyType">When set, skip the other section queries
    /// entirely and return all matches for the chosen type. Used by the
    /// per-type filter chips on /Search.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchResultType? onlyType = null,
        CancellationToken ct = default);
}
