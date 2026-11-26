using Humans.Search.Models;
using Humans.Search.Services;
using Humans.Search.Services.Dtos;
using Humans.Base.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Users.Contracts;

namespace Humans.Search.Controllers;

[Authorize]
[Route("Search")]
internal sealed class SearchController(
    ISearchService searchService,
    IUserService userService,
    ILogger<SearchController> logger) : HumansControllerBase(userService)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? q,
        string? filter,
        CancellationToken ct)
    {
        var trimmed = (q ?? string.Empty).Trim();
        try
        {
            var results = await searchService.SearchAsync(trimmed, filter, ct);
            return View(BuildViewModel(trimmed.Length < SearchService.MinQueryLength ? q : results.Query, results));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Global search request cancelled");
            // User navigated away — let ASP.NET handle it (don't return a 200 shell).
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Global search failed for query {Query}", trimmed);
            // Keep the form usable, but do not report an incomplete search as no matches.
            return View(new GlobalSearchViewModel { Query = trimmed, Filter = filter, SearchFailed = true });
        }
    }

    private static GlobalSearchViewModel BuildViewModel(string? query, GlobalSearchResults results) =>
        new()
        {
            Query = query,
            Filter = results.Filter,
            Buckets = results.Buckets
                .OrderBy(b => b.Weight).ThenBy(b => b.Key, StringComparer.Ordinal)
                .Select(b => b with
                {
                    Hits = b.Hits
                        .OrderByDescending(h => h.Score)
                        .ThenBy(h => h.SortKey, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                })
                .ToList(),
        };
}
