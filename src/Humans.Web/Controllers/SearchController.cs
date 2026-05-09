using Humans.Application.DTOs;
using Humans.Application.Interfaces.Search;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Top-level "search the whole app" page. Aggregates ranked, type-grouped
/// hits across humans, teams, camps, and shifts (rotas). The auth boundary
/// for admin-only profile fields lives here, not in
/// <see cref="ISearchService"/> — services are auth-free per design-rules
/// §11.
/// </summary>
[Authorize]
[Route("Search")]
public sealed class SearchController : HumansControllerBase
{
    private readonly ISearchService _searchService;
    private readonly ILogger<SearchController> _logger;

    /// <summary>
    /// Roles that unlock <see cref="PersonSearchFields.AdminAll"/> on the
    /// human search bucket. These are the same roles that have a route to
    /// see admin-only profile data (verified emails, non-public ContactFields)
    /// from elsewhere in the app, so the global search must not be a path
    /// to additional disclosure.
    /// </summary>
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
    /// orchestrator and renders ranked, type-grouped results.
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? q,
        SearchResultType? filter,
        CancellationToken ct)
    {
        var viewModel = new GlobalSearchViewModel { Query = q, Filter = filter };

        var trimmed = (q ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            // Empty / single-character query: render the instructional
            // placeholder. Returning the bare view-model keeps the URL
            // stable so users can refine without losing the chip selection.
            return View(viewModel);
        }

        try
        {
            var includeAdmin = AdminViewerRoles.Any(role => User.IsInRole(role));
            var results = await _searchService.SearchAsync(
                trimmed,
                filter,
                includeAdmin,
                perTypeLimit: 10,
                ct);

            return View(new GlobalSearchViewModel
            {
                Query = results.Query,
                Filter = filter,
                Results = results.Results,
                HumanCount = results.HumanCount,
                TeamCount = results.TeamCount,
                CampCount = results.CampCount,
                ShiftCount = results.ShiftCount,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Global search failed for query {Query}", trimmed);
            // Return the empty view-model so the user sees the search page
            // shell instead of a 500. The query input is preserved so they
            // can retry / refine.
            return View(viewModel);
        }
    }
}
