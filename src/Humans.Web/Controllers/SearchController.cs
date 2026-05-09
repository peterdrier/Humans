using Humans.Application.DTOs;
using Humans.Application.Interfaces.Search;
using Humans.Domain.Constants;
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
/// The auth boundary lives here, not in <see cref="ISearchService"/>;
/// services receive a resolved <see cref="SearchScope"/> per design-rules
/// §11.
/// </summary>
[Authorize]
[Route("Search")]
public sealed class SearchController : HumansControllerBase
{
    private readonly ISearchService _searchService;
    private readonly ILogger<SearchController> _logger;

    /// <summary>
    /// Roles that unlock <see cref="SearchScope.Admin"/> across ALL four
    /// buckets (humans, teams, camps, shifts).
    /// </summary>
    /// <remarks>
    /// Known limitation re: <c>memory/code/admin-role-superset.md</c> —
    /// <c>TeamsAdmin</c> / <c>CampAdmin</c> / <c>TicketAdmin</c> are
    /// intentionally excluded here even though each can see hidden teams /
    /// non-public camp seasons / admin-only rotas in their own admin
    /// pages. A single <see cref="SearchScope"/> value drives all four
    /// buckets at once, so promoting a TeamsAdmin to <c>Admin</c> would
    /// also expose admin-only profile fields (verified emails, non-public
    /// ContactFields) in the human bucket — a real privilege concern. The
    /// proper fix is per-bucket scope resolution
    /// (nobodies-collective/Humans#693). Until then, domain admins use the
    /// volunteer-visible search surface.
    /// </remarks>
    private static readonly string[] AdminViewerRoles =
    {
        RoleNames.Admin,
        RoleNames.HumanAdmin,
        RoleNames.Board,
    };

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
                trimmed, ResolveScope(), filter, PerTypeLimit(filter), ct);
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

    private SearchScope ResolveScope() =>
        AdminViewerRoles.Any(role => User.IsInRole(role))
            ? SearchScope.Admin
            : SearchScope.Public;

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
