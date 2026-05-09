using Humans.Application.DTOs;
using Humans.Application.Interfaces.Search;
using Humans.Domain.Entities;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Top-level "search the whole app" page. Aggregates name-only hits across
/// humans, teams, camps, and shifts (rotas) into four type-grouped sections.
/// Every authenticated viewer sees the same public-visibility surface;
/// privileged search across hidden teams, non-public camp seasons, or
/// admin-only profile fields is out of scope (see
/// <c>docs/features/global-search.md</c>).
/// </summary>
[Authorize]
[Route("Search")]
public sealed class SearchController : HumansControllerBase
{
    private readonly ISearchService _searchService;
    private readonly ILogger<SearchController> _logger;

    public SearchController(
        ISearchService searchService,
        UserManager<User> userManager,
        ILogger<SearchController> logger)
        : base(userManager)
    {
        _searchService = searchService;
        _logger = logger;
    }

    /// <summary>
    /// Render the global search page. Empty/short query renders the
    /// instructional placeholder; a real query fans out through the
    /// orchestrator and renders type-grouped results.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? q,
        SearchResultType? filter,
        CancellationToken ct)
    {
        var trimmed = (q ?? string.Empty).Trim();
        if (trimmed.Length < 2)
            return View(new GlobalSearchViewModel { Query = q, Filter = filter });

        return View(await RunSearchAsync(trimmed, filter, ct));
    }

    private async Task<GlobalSearchViewModel> RunSearchAsync(
        string trimmed, SearchResultType? filter, CancellationToken ct)
    {
        try
        {
            var results = await _searchService.SearchAsync(
                trimmed, filter, PerTypeLimit(filter), ct);
            return BuildViewModel(results, filter);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Global search failed for query {Query}", trimmed);
            // Render the page shell instead of a 500; preserve the query so
            // the user can refine it.
            return new GlobalSearchViewModel { Query = trimmed, Filter = filter };
        }
    }

    // When a filter chip is active we want a deeper bucket for that type;
    // the unified view stays at perTypeLimit=10 across all four.
    private static int PerTypeLimit(SearchResultType? filter) =>
        filter.HasValue ? 50 : 10;

    private static GlobalSearchViewModel BuildViewModel(
        GlobalSearchResults results, SearchResultType? filter) =>
        new()
        {
            Query = results.Query,
            Filter = filter,
            // Display ordering belongs at the controller per
            // memory/architecture/display-sort-in-controllers.md. Humans
            // sort by BurnerName asc (matches /Profile/Search); the other
            // three buckets sort by Score desc then Title asc.
            HumanResults = results.Humans
                .OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.ToHumanSearchViewModel())
                .ToList(),
            TeamResults = SortByScore(results.Teams),
            CampResults = SortByScore(results.Camps),
            ShiftResults = SortByScore(results.Shifts),
        };

    private static IReadOnlyList<GlobalSearchResult> SortByScore(
        IReadOnlyList<GlobalSearchResult> bucket) =>
        bucket
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
