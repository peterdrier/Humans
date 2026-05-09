using Humans.Application.DTOs;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Search;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Profiles;

namespace Humans.Application.Services.Search;

/// <summary>
/// Application-layer implementation of <see cref="ISearchService"/>. Names-only
/// orchestrator: each section's own service runs the case-insensitive Postgres
/// ILike query at the DB layer (per
/// <c>memory/feedback_ef_ilike_not_toupper.md</c>) and returns its own hit
/// shape; this orchestrator scores within each type and returns four
/// independently-ranked buckets. There is no cross-modal / relational
/// expansion (see <c>docs/features/global-search.md</c>).
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly ICampService _campService;
    private readonly IShiftManagementService _shiftService;

    // Score band — multi-field matches receive a small additive boost so a
    // name+description hit ranks above a description-only hit. Title-equals
    // beats title-startswith beats title-contains beats body-contains.
    private const int ScoreExactName = 100;
    private const int ScorePrefixName = 80;
    private const int ScoreContainsName = 60;
    private const int ScoreSlugMatch = 70;
    private const int ScoreContainsBody = 30;
    private const int ScoreMultiFieldBoost = 15;

    public SearchService(
        IProfileService profileService,
        ITeamService teamService,
        ICampService campService,
        IShiftManagementService shiftService)
    {
        _profileService = profileService;
        _teamService = teamService;
        _campService = campService;
        _shiftService = shiftService;
    }

    public async Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchScope scope = SearchScope.Public,
        SearchResultType? onlyType = null,
        int perTypeLimit = 10,
        CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            return new GlobalSearchResults(
                trimmed,
                Array.Empty<HumanSearchResult>(),
                Array.Empty<GlobalSearchResult>(),
                Array.Empty<GlobalSearchResult>(),
                Array.Empty<GlobalSearchResult>());
        }

        var humans = onlyType is null or SearchResultType.Human
            ? await SearchHumansAsync(trimmed, scope, perTypeLimit, ct)
            : Array.Empty<HumanSearchResult>();
        var teams = onlyType is null or SearchResultType.Team
            ? await SearchTeamsAsync(trimmed, scope, perTypeLimit, ct)
            : Array.Empty<GlobalSearchResult>();
        var camps = onlyType is null or SearchResultType.Camp
            ? await SearchCampsAsync(trimmed, scope, perTypeLimit, ct)
            : Array.Empty<GlobalSearchResult>();
        var shifts = onlyType is null or SearchResultType.Shift
            ? await SearchShiftsAsync(trimmed, scope, perTypeLimit, ct)
            : Array.Empty<GlobalSearchResult>();

        return new GlobalSearchResults(trimmed, humans, teams, camps, shifts);
    }

    private async Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(
        string query, SearchScope scope, int limit, CancellationToken ct)
    {
        // PersonSearchFields enum carries the public/admin split for humans
        // per memory/architecture/person-search.md; SearchScope translates
        // to it here. Display ordering is the matcher's own scoring inside
        // ProfileService — the global-search view renders each bucket via
        // the canonical _HumanSearchResults partial after a controller-side
        // BurnerName sort, matching /Profile/Search.
        var fields = scope == SearchScope.Admin
            ? PersonSearchFields.AdminAll
            : PersonSearchFields.PublicAll;
        return await _profileService.SearchProfilesAsync(query, fields, limit, ct);
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchTeamsAsync(
        string query, SearchScope scope, int limit, CancellationToken ct)
    {
        var hits = await _teamService.SearchAsync(query, scope, limit, ct);
        return hits
            .Select(t =>
            {
                var score = ScoreEntityMatch(query, t.Name, t.Slug, t.Description, out var matchField);
                return new GlobalSearchResult(
                    Type: SearchResultType.Team,
                    Title: t.Name,
                    Subtitle: ComposeSnippet(query, t.Description) ?? t.Slug,
                    Url: $"/Teams/{t.Slug}",
                    Score: score,
                    MatchField: matchField);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchCampsAsync(
        string query, SearchScope scope, int limit, CancellationToken ct)
    {
        var hits = await _campService.SearchAsync(query, scope, limit, ct);
        return hits
            .Select(c =>
            {
                var score = ScoreEntityMatch(query, c.Name, c.Slug, c.Blurb, out var matchField);
                return new GlobalSearchResult(
                    Type: SearchResultType.Camp,
                    Title: c.Name,
                    Subtitle: ComposeSnippet(query, c.Blurb) ?? c.Slug,
                    Url: $"/Camps/{c.Slug}",
                    Score: score,
                    MatchField: matchField);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchShiftsAsync(
        string query, SearchScope scope, int limit, CancellationToken ct)
    {
        // The "shift" search hit is a Rota (named, role-shaped grouping of
        // shifts) — an individual Shift row is a date+time slot with no
        // human-readable title to match against.
        var hits = await _shiftService.SearchAsync(query, scope, limit, ct);
        return hits
            .Select(r =>
            {
                var score = ScoreEntityMatch(query, r.Name, slug: null, r.Description, out var matchField);
                return new GlobalSearchResult(
                    Type: SearchResultType.Shift,
                    Title: r.Name,
                    Subtitle: ComposeSnippet(query, r.Description) ?? r.TeamName,
                    Url: $"/Shifts?departmentId={r.TeamId}",
                    Score: score,
                    MatchField: matchField);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Score an entity by which fields match. Returns 0 when nothing
    /// matches — caller should drop those rows. Multi-field matches receive
    /// a small additive boost so a name+description hit ranks above a
    /// description-only hit. Sets <paramref name="matchField"/> to the
    /// field that produced the highest single-field contribution.
    /// </summary>
    private static int ScoreEntityMatch(
        string query, string name, string? slug, string? body, out string matchField)
    {
        matchField = string.Empty;
        var total = 0;
        var matchCount = 0;

        var nameScore = ScoreNameField(name, query);
        if (nameScore > 0)
        {
            total += nameScore;
            matchField = "Name";
            matchCount++;
        }

        if (!string.IsNullOrEmpty(slug)
            && slug.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            total += ScoreSlugMatch;
            if (matchField.Length == 0) matchField = "Slug";
            matchCount++;
        }

        if (!string.IsNullOrEmpty(body)
            && body.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            total += ScoreContainsBody;
            if (matchField.Length == 0) matchField = "Description";
            matchCount++;
        }

        if (matchCount > 1) total += ScoreMultiFieldBoost;
        return total;
    }

    private static int ScoreNameField(string name, string query)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return ScoreExactName;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return ScorePrefixName;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return ScoreContainsName;
        return 0;
    }

    private static string? ComposeSnippet(string query, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var idx = body.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        const int radius = 40;
        var start = Math.Max(0, idx - radius);
        var end = Math.Min(body.Length, idx + query.Length + radius);
        var snippet = body[start..end].Trim();
        if (start > 0) snippet = "…" + snippet;
        if (end < body.Length) snippet = snippet + "…";
        return snippet;
    }
}
