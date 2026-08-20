using Humans.Base.Extensions;
using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Search.Services.Dtos;
using Microsoft.Extensions.Configuration;

namespace Humans.Search.Services;

/// <summary>
/// Per-entity-field search orchestrator (no cross-modal traversal): each section matches and scores its own hits, this returns five buckets of keys (unsorted).
/// Events is the one bucket still scored here — it publishes no scored search hit (nobodies-collective/Humans#1062).
/// See docs/features/global/global-search.md. Display ordering lives in SearchController.
/// </summary>
internal sealed class SearchService(
    IUserServiceRead userService,
    ITeamServiceRead teamService,
    ICampServiceRead campService,
    IShiftManagementServiceRead shiftService,
    IEventServiceRead eventService,
    IConfiguration configuration) : ISearchService
{
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
        return hits
            .Select(t => new GlobalSearchResult(
                Type: SearchResultType.Team,
                Key: t.TeamId,
                SortKey: t.Name,
                Score: t.Score))
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchCampsAsync(
        string query, int limit, CancellationToken ct)
    {
        var hits = await campService.SearchAsync(query, limit, ct);
        return hits
            .Select(c => new GlobalSearchResult(
                Type: SearchResultType.Camp,
                Key: c.CampId,
                SortKey: c.Name,
                Score: c.Score))
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchShiftsAsync(
        string query, int limit, CancellationToken ct)
    {
        // Hits are Rotas (named shift groupings), not individual Shift rows (which have no title).
        var hits = await shiftService.SearchAsync(query, limit, ct);
        return hits
            .Select(r => new GlobalSearchResult(
                Type: SearchResultType.Shift,
                Key: r.RotaId,
                SortKey: r.Name,
                Score: r.Score))
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchEventsAsync(
        string query, int limit, CancellationToken ct)
    {
        // Reuse the public Browse query — Approved-only, filtered server-side by title/description.
        // Scoring stays here, unlike the other three buckets: Events publishes no scored search hit
        // to move it onto (nobodies-collective/Humans#1062). Rows that only matched via Description
        // score 0 on Title and fall through to the contains tier so they still appear.
        var hits = await eventService.GetApprovedEventsAsync(
            campId: null, venueId: null, categoryId: null,
            q: query, excludedSlugs: Array.Empty<string>(), ct);

        return hits
            .Select(e =>
            {
                var titleScore = e.Title.NameMatchScore(query);
                return new GlobalSearchResult(
                    Type: SearchResultType.Event,
                    Key: e.Id,
                    SortKey: e.Title,
                    Score: titleScore > 0 ? titleScore : StringSearchExtensions.ContainsNameScore);
            })
            .OrderByDescending(r => r.Score) // arch:db-sort-ok top-N relevance selector
            .Take(limit)
            .ToList();
    }
}
