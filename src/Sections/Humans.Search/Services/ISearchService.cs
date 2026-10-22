using Humans.Base.Interfaces;
using Humans.Search.Services.Dtos;

namespace Humans.Search.Services;

/// <summary>
/// Not an authorization boundary: a GUID query resolves straight to the entity, and the destination page decides
/// whether the viewer may open it.
/// </summary>
internal interface ISearchService : IOrchestrator
{
    /// <summary>
    /// A <paramref name="query"/> under 2 characters after trim returns empty results without calling any section.
    /// <paramref name="onlyType"/> queries only that section.
    /// </summary>
    Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchResultType? onlyType = null,
        CancellationToken ct = default);
}
