using Humans.Base.Interfaces;

namespace Humans.Search.Services.Dtos;

/// <summary>
/// One contributed category and its hits, unsorted. <see cref="Hits"/> is empty when the category was not searched:
/// a query under 2 characters, or a filter naming another category.
/// </summary>
internal sealed record SearchBucket(
    string Key,
    int Weight,
    string Label,
    string IconCssClass,
    Type ResultComponent,
    IReadOnlyList<SearchHit> Hits);

/// <summary>
/// Every enabled category, unordered, carrying its display weight. <see cref="Filter"/> is the matched category key, null when the request
/// named none or one that is not enabled.
/// </summary>
internal sealed record GlobalSearchResults(
    string Query,
    string? Filter,
    IReadOnlyList<SearchBucket> Buckets);
