using Humans.Application.DTOs;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Search;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;

namespace Humans.Application.Services.Search;

/// <summary>
/// Application-layer implementation of <see cref="ISearchService"/>. Names-only
/// orchestrator: each section's own service runs the case-insensitive Postgres
/// ILike query against the entity's name field at the DB layer (per
/// <c>memory/feedback_ef_ilike_not_toupper.md</c>) and returns its own hit
/// shape; this orchestrator scores each hit by name-match strength and
/// returns four type-grouped buckets. There is no cross-modal / relational
/// expansion and no matching beyond the name field on the entity itself
/// (see <c>docs/features/global/global-search.md</c>).
/// </summary>
/// <remarks>
/// Display ordering is a presentation concern and lives in
/// <c>SearchController.BuildViewModel</c> per
/// <c>memory/architecture/display-sort-in-controllers.md</c> — the buckets
/// returned here are scored but unsorted.
/// </remarks>
public sealed class SearchService : ISearchService
{
    private readonly IUserService _userService;
    private readonly ITeamService _teamService;
    private readonly ICampService _campService;
    private readonly IShiftManagementService _shiftService;

    // Name-match scoring: exact > prefix > contains.
    private const int ScoreExactName = 100;
    private const int ScorePrefixName = 80;
    private const int ScoreContainsName = 60;

    public SearchService(
        IUserService userService,
        ITeamService teamService,
        ICampService campService,
        IShiftManagementService shiftService)
    {
        _userService = userService;
        _teamService = teamService;
        _campService = campService;
        _shiftService = shiftService;
    }

    public async Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchResultType? onlyType = null,
        int perTypeLimit = 10,
        CancellationToken ct = default)
    {
        var trimmed = query.Trim();
        if (trimmed.Length < 2)
        {
            return new GlobalSearchResults(
                trimmed,
                [],
                [],
                [],
                []);
        }

        var humans = onlyType is null or SearchResultType.Human
            ? await SearchHumansAsync(trimmed, perTypeLimit, ct)
            : Array.Empty<HumanSearchResult>();
        var teams = onlyType is null or SearchResultType.Team
            ? await SearchTeamsAsync(trimmed, perTypeLimit, ct)
            : Array.Empty<GlobalSearchResult>();
        var camps = onlyType is null or SearchResultType.Camp
            ? await SearchCampsAsync(trimmed, perTypeLimit, ct)
            : Array.Empty<GlobalSearchResult>();
        var shifts = onlyType is null or SearchResultType.Shift
            ? await SearchShiftsAsync(trimmed, perTypeLimit, ct)
            : Array.Empty<GlobalSearchResult>();

        return new GlobalSearchResults(trimmed, humans, teams, camps, shifts);
    }

    private async Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(
        string query, int limit, CancellationToken ct)
    {
        // Every viewer sees the public surface — admin profile fields
        // (verified emails, non-public ContactFields) never surface from
        // /Search regardless of role.
        return await _userService.SearchUsersAsync(
            query, PersonSearchFields.PublicAll, limit, ct);
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchTeamsAsync(
        string query, int limit, CancellationToken ct)
    {
        var hits = await _teamService.SearchAsync(query, limit, ct);
        return hits
            .Select(t => new GlobalSearchResult(
                Type: SearchResultType.Team,
                Title: t.Name,
                Subtitle: t.Slug,
                Url: $"/Teams/{t.Slug}",
                Score: ScoreNameField(t.Name, query)))
            .Where(r => r.Score > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchCampsAsync(
        string query, int limit, CancellationToken ct)
    {
        var hits = await _campService.SearchAsync(query, limit, ct);
        return hits
            .Select(c => new GlobalSearchResult(
                Type: SearchResultType.Camp,
                Title: c.Name,
                Subtitle: c.Slug,
                Url: $"/Camps/{c.Slug}",
                Score: ScoreNameField(c.Name, query)))
            .Where(r => r.Score > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchShiftsAsync(
        string query, int limit, CancellationToken ct)
    {
        // The "shift" search hit is a Rota (named, role-shaped grouping of
        // shifts) — an individual Shift row is a date+time slot with no
        // human-readable title to match against.
        var hits = await _shiftService.SearchAsync(query, limit, ct);
        return hits
            .Select(r => new GlobalSearchResult(
                Type: SearchResultType.Shift,
                Title: r.Name,
                Subtitle: r.TeamName,
                Url: $"/Shifts?departmentId={r.TeamId}",
                Score: ScoreNameField(r.Name, query)))
            .Where(r => r.Score > 0)
            .ToList();
    }

    private static int ScoreNameField(string name, string query)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return ScoreExactName;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return ScorePrefixName;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return ScoreContainsName;
        return 0;
    }
}
