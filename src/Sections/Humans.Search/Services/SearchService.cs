using Humans.Base.Interfaces;
using Humans.Search.Services.Dtos;

namespace Humans.Search.Services;

/// <summary>Fans out to every section's <see cref="ISearchContributor"/> categories; names none of them.</summary>
internal sealed class SearchService(
    IEnumerable<ISearchContributor> contributors,
    IServiceProvider services) : ISearchService
{
    internal const int MinQueryLength = 2;

    public async Task<GlobalSearchResults> SearchAsync(
        string query,
        string? filter = null,
        CancellationToken ct = default)
    {
        var trimmed = query.Trim();
        var categories = contributors
            .SelectMany(c => c.Categories())
            .Where(c => c.IsEnabled?.Invoke(services) ?? true)
            .ToList();
        var only = categories.FirstOrDefault(c => string.Equals(c.Key, filter, StringComparison.OrdinalIgnoreCase))?.Key;

        var buckets = new List<SearchBucket>(categories.Count);
        foreach (var category in categories)
        {
            var searched = trimmed.Length >= MinQueryLength && (only is null || string.Equals(only, category.Key, StringComparison.Ordinal));
            buckets.Add(new SearchBucket(
                category.Key,
                category.Weight,
                category.Label(services),
                category.IconCssClass,
                category.ResultComponent,
                searched ? await category.SearchAsync(services, trimmed, ct) : []));
        }

        return new GlobalSearchResults(trimmed, only, buckets);
    }
}
