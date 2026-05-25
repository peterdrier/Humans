using Humans.Application.DTOs;
using Humans.Application.Interfaces.Search;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Humans.Application.Interfaces.Users;

namespace Humans.Web.Controllers;

/// <summary>Global search: name-only hits across humans/teams/camps/rotas/events. Public-visibility surface only (docs/features/global/global-search.md).</summary>
[Authorize]
[Route("Search")]
public sealed class SearchController(
    ISearchService searchService,
    IUserServiceRead userService,
    ILogger<SearchController> logger) : HumansControllerBase(userService)
{
    /// <summary>Global search page. Short query → placeholder; otherwise fans out and renders type-grouped results.</summary>
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
            var results = await searchService.SearchAsync(trimmed, filter, ct);
            return BuildViewModel(results, filter);
        }
        catch (OperationCanceledException)
        {
            // User navigated away — let ASP.NET handle it (don't return a 200 shell).
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Global search failed for query {Query}", trimmed);
            // Page shell instead of 500; preserve query so user can refine.
            return new GlobalSearchViewModel { Query = trimmed, Filter = filter };
        }
    }

    private static GlobalSearchViewModel BuildViewModel(
        GlobalSearchResults results, SearchResultType? filter) =>
        new()
        {
            Query = results.Query,
            Filter = filter,
            // Display sort lives in controller (display-sort-in-controllers): humans by BurnerName, others by Score desc + Title asc.
            HumanResults = results.Humans
                .OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.ToHumanSearchViewModel())
                .ToList(),
            TeamResults = SortByScore(results.Teams),
            CampResults = SortByScore(results.Camps),
            ShiftResults = SortByScore(results.Shifts),
            EventResults = SortByScore(results.Events),
        };

    private static IReadOnlyList<GlobalSearchResult> SortByScore(
        IReadOnlyList<GlobalSearchResult> bucket) =>
        bucket
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
