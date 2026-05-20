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
/// expansion (see <c>docs/features/global/global-search.md</c>).
///
/// <para>
/// Per design-rules §6, this service NEVER queries another section's
/// tables directly — it only calls the public service interface for each
/// section.
/// </para>
///
/// <para>
/// Every viewer sees the public-visibility surface: hidden teams,
/// non-public camp seasons, admin-only rotas, and admin-only profile
/// fields are excluded for everyone, regardless of role. Privileged
/// search across those surfaces is out of scope for this iteration —
/// admins use the existing per-section admin pages.
/// </para>
/// </summary>
public interface ISearchService : IApplicationService
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
