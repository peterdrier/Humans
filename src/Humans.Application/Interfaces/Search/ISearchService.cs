using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Search;

/// <summary>
/// Top-level search orchestrator for the global <c>/Search</c> page. Fans
/// out to per-section service interfaces (<c>IProfileService</c>,
/// <c>ITeamService</c>, <c>ICampService</c>, <c>IShiftManagementService</c>),
/// each of which runs its own case-insensitive Postgres ILike query at the
/// DB layer (<c>memory/feedback_ef_ilike_not_toupper.md</c>). The
/// orchestrator scores and ranks within each type and returns four
/// independently-ranked buckets — there is no cross-modal / relational
/// expansion (see <c>docs/features/global-search.md</c>).
///
/// <para>
/// Per design-rules §6, this service NEVER queries another section's
/// tables directly — it only calls the public service interface for each
/// section.
/// </para>
///
/// <para>
/// Authorization is the controller's job. The <see cref="SearchScope"/>
/// argument is set by the controller after role verification; services
/// are auth-free per design-rules §11.
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
    /// <param name="scope">Public viewer or admin viewer. Drives the
    /// hidden-team filter, the camp public-status filter, the rota
    /// volunteer-visibility filter, and the
    /// <c>PersonSearchFields.PublicAll</c> vs <c>AdminAll</c> bit-flag
    /// handed to <c>IProfileService</c>.</param>
    /// <param name="onlyType">When set, skip the other three section
    /// queries entirely and return all matches for the chosen type up to
    /// <paramref name="perTypeLimit"/>. Used by the per-type filter chips
    /// on /Search.</param>
    /// <param name="perTypeLimit">Maximum hits per type bucket.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchScope scope = SearchScope.Public,
        SearchResultType? onlyType = null,
        int perTypeLimit = 10,
        CancellationToken ct = default);
}
