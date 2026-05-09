using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Search;

/// <summary>
/// Top-level search orchestrator for the global <c>/Search</c> page. Fans
/// out to per-section service interfaces (<c>IProfileService</c>,
/// <c>ITeamService</c>, <c>ICampService</c>, <c>IShiftManagementService</c>),
/// merges and ranks results, and adds cross-modal "relational" hits (a
/// matched person's teams/camps; a matched team's rotas).
///
/// <para>
/// Per design-rules §6, this service NEVER queries another section's
/// tables directly — it only calls the public service interface for each
/// section. Each section owns the search shape (which fields match, how
/// to score) for its own data; this orchestrator only merges and ranks.
/// </para>
///
/// <para>
/// Authorization is the controller's job. Non-admin endpoints pass
/// <c>includeAdmin=false</c>; admin-bit profile fields (verified emails,
/// non-public ContactFields) are then never reached. The auth boundary is
/// in the controller, never in the service.
/// </para>
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Run a global search. Empty/whitespace <paramref name="query"/>, or
    /// shorter than 2 characters after trim, returns an empty
    /// <see cref="GlobalSearchResults"/>.
    /// </summary>
    /// <param name="query">User-entered text. Trimmed and matched
    /// case-insensitively per <c>memory/feedback_ef_ilike_not_toupper.md</c>.</param>
    /// <param name="filter">When set, scope the merged list to a single
    /// type (cross-modal relational hits are still pulled and listed only
    /// if they match the filter).</param>
    /// <param name="includeAdmin">Pass true only from controllers that
    /// have already authorized admin access. Forwards
    /// <c>PersonSearchFields.AdminAll</c> to <c>IProfileService</c>.</param>
    /// <param name="perTypeLimit">Maximum number of direct hits per
    /// section in the unified view. Used as a presentation cap; per-type
    /// pages can request a larger limit.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchResultType? filter = null,
        bool includeAdmin = false,
        int perTypeLimit = 10,
        CancellationToken ct = default);
}

/// <summary>
/// Aggregated output of a single global-search call. Counts are post-
/// dedup so the "See all 47 humans" link can use them verbatim.
/// </summary>
/// <param name="Query">Echo of the trimmed input.</param>
/// <param name="Results">Merged list, already ordered by Score desc then
/// Title asc.</param>
/// <param name="HumanCount">Total humans matched (direct + relational),
/// pre-cap.</param>
/// <param name="TeamCount">Total teams matched, pre-cap.</param>
/// <param name="CampCount">Total camps matched, pre-cap.</param>
/// <param name="ShiftCount">Total rotas (shifts) matched, pre-cap.</param>
public record GlobalSearchResults(
    string Query,
    IReadOnlyList<GlobalSearchResult> Results,
    int HumanCount,
    int TeamCount,
    int CampCount,
    int ShiftCount);
