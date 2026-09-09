using Humans.Base.Interfaces;
using Humans.Search.Services.Dtos;

namespace Humans.Search.Services;

/// <summary>
/// Orchestrator for the global <c>/Search</c> page: fans out to five sections'
/// read interfaces and returns their scored hits as five buckets, untouched.
/// Search is not an authorization boundary — text queries reach each section's
/// public surface only, a GUID query resolves straight to the entity, and the
/// destination page decides whether the viewer may open it.
/// Invariants: <c>Docs/Search.md</c>.
/// </summary>
internal interface ISearchService : IOrchestrator
{
    /// <summary>
    /// Run a global search. A <paramref name="query"/> shorter than 2 characters
    /// after trim returns an empty <see cref="GlobalSearchResults"/> without
    /// calling any section.
    /// </summary>
    /// <param name="query">User-entered text; trimmed before matching.</param>
    /// <param name="onlyType">When set, only that section is queried. Backs the
    /// per-type filter chips.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchResultType? onlyType = null,
        CancellationToken ct = default);
}
