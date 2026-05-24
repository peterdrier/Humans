using Humans.Application.DTOs;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Events;
using Humans.Application.Interfaces.Search;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Microsoft.Extensions.Configuration;

namespace Humans.Application.Services.Search;

/// <summary>
/// Names-only search orchestrator: each section runs its own ILike, this scores hits and returns five buckets (unsorted).
/// See docs/features/global/global-search.md. Display ordering lives in SearchController.
/// </summary>
public sealed class SearchService(
    IUserServiceRead userService,
    ITeamServiceRead teamService,
    ICampServiceRead campService,
    IShiftManagementService shiftService,
    IEventService eventService,
    IConfiguration configuration) : ISearchService
{
    // Name-match scoring: exact > prefix > contains.
    private const int ScoreExactName = 100;
    private const int ScorePrefixName = 80;
    private const int ScoreContainsName = 60;

    private readonly bool _eventsFeatureEnabled = configuration.GetValue<bool>("Features:Events");

    // No per-type cap: at ~500-user scale a name match returns a handful of rows,
    // and capping made people miss matches (issue: too-hard-to-find-people). Each
    // section's SearchAsync still takes a max, so pass an effectively-unbounded one.
    private const int Unlimited = int.MaxValue;

    public async Task<GlobalSearchResults> SearchAsync(
        string query,
        SearchResultType? onlyType = null,
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
                [],
                []);
        }

        var humans = onlyType is null or SearchResultType.Human
            ? await SearchHumansAsync(trimmed, Unlimited, ct)
            : Array.Empty<HumanSearchResult>();
        var teams = onlyType is null or SearchResultType.Team
            ? await SearchTeamsAsync(trimmed, Unlimited, ct)
            : Array.Empty<GlobalSearchResult>();
        var camps = onlyType is null or SearchResultType.Camp
            ? await SearchCampsAsync(trimmed, Unlimited, ct)
            : Array.Empty<GlobalSearchResult>();
        var shifts = onlyType is null or SearchResultType.Shift
            ? await SearchShiftsAsync(trimmed, Unlimited, ct)
            : Array.Empty<GlobalSearchResult>();
        var events = _eventsFeatureEnabled && onlyType is null or SearchResultType.Event
            ? await SearchEventsAsync(trimmed, Unlimited, ct)
            : Array.Empty<GlobalSearchResult>();

        return new GlobalSearchResults(trimmed, humans, teams, camps, shifts, events);
    }

    private async Task<IReadOnlyList<HumanSearchResult>> SearchHumansAsync(
        string query, int limit, CancellationToken ct)
    {
        // Public surface only — admin fields never reach /Search regardless of role.
        return await userService.SearchUsersAsync(
            query, PersonSearchFields.PublicAll, limit, ct);
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchTeamsAsync(
        string query, int limit, CancellationToken ct)
    {
        var hits = await teamService.SearchAsync(query, limit, ct);
        var isGuidQuery = Guid.TryParse(query, out _);
        return hits
            .Select(t => new GlobalSearchResult(
                Type: SearchResultType.Team,
                Title: t.Name,
                Subtitle: t.Slug,
                Url: $"/Teams/{t.Slug}",
                Score: isGuidQuery ? ScoreExactName : ScoreNameField(t.Name, query)))
            .Where(r => r.Score > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchCampsAsync(
        string query, int limit, CancellationToken ct)
    {
        var hits = await campService.SearchAsync(query, limit, ct);
        var isGuidQuery = Guid.TryParse(query, out _);
        return hits
            .Select(c => new GlobalSearchResult(
                Type: SearchResultType.Camp,
                Title: c.Name,
                Subtitle: c.Slug,
                Url: $"/Camps/{c.Slug}",
                Score: isGuidQuery ? ScoreExactName : ScoreNameField(c.Name, query)))
            .Where(r => r.Score > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchShiftsAsync(
        string query, int limit, CancellationToken ct)
    {
        // Hits are Rotas (named shift groupings), not individual Shift rows (which have no title).
        var hits = await shiftService.SearchAsync(query, limit, ct);
        var isGuidQuery = Guid.TryParse(query, out _);
        return hits
            .Select(r => new GlobalSearchResult(
                Type: SearchResultType.Shift,
                Title: r.Name,
                Subtitle: r.TeamName,
                Url: $"/Shifts?departmentId={r.TeamId}",
                Score: isGuidQuery ? ScoreExactName : ScoreNameField(r.Name, query)))
            .Where(r => r.Score > 0)
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchEventsAsync(
        string query, int limit, CancellationToken ct)
    {
        // Reuse the public Browse query — Approved-only, filtered server-side by title/description.
        // We re-score on Title here so the global "exact > prefix > contains" rubric still ranks the
        // bucket; rows that only matched via Description fall through to a contains score so they
        // still appear (matches what users expect from event search, which is more free-form than
        // the other entity types).
        var hits = await eventService.GetApprovedEventsAsync(
            campId: null, venueId: null, categoryId: null,
            q: query, excludedSlugs: Array.Empty<string>(), ct);

        return hits
            .Select(e =>
            {
                var titleScore = ScoreNameField(e.Title, query);
                return new GlobalSearchResult(
                    Type: SearchResultType.Event,
                    Title: e.Title,
                    Subtitle: e.Category?.Name ?? string.Empty,
                    Url: $"/Events/Browse?q={Uri.EscapeDataString(e.Title)}",
                    Score: titleScore > 0 ? titleScore : ScoreContainsName);
            })
            .OrderByDescending(r => r.Score) // arch:db-sort-ok top-N relevance selector
            .Take(limit)
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
