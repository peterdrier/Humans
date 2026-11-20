using Humans.Camps.Contracts;
using Humans.Events.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Search.Services.Dtos;
using Microsoft.Extensions.Configuration;

namespace Humans.Search.Services;

internal sealed class SearchService(
    IUserService userService,
    ITeamService teamService,
    ICampService campService,
    IShiftService shiftService,
    IEventService eventService,
    IConfiguration configuration) : ISearchService
{
    private readonly bool _eventsFeatureEnabled = configuration.GetValue<bool>("Features:Events");

    // No per-type cap: capping hid people. Each section's SearchAsync still takes a max.
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

        // Public surface only — admin fields never reach /Search regardless of role.
        var humans = onlyType is null or SearchResultType.Human
            ? await userService.SearchUsersAsync(trimmed, PersonSearchFields.PublicAll, Unlimited, ct)
            : Array.Empty<HumanSearchResult>();
        var teams = onlyType is null or SearchResultType.Team
            ? await SearchTeamsAsync(trimmed, ct)
            : Array.Empty<GlobalSearchResult>();
        var camps = onlyType is null or SearchResultType.Camp
            ? await SearchCampsAsync(trimmed, ct)
            : Array.Empty<GlobalSearchResult>();
        var shifts = onlyType is null or SearchResultType.Shift
            ? await SearchShiftsAsync(trimmed, ct)
            : Array.Empty<GlobalSearchResult>();
        var events = _eventsFeatureEnabled && (onlyType is null or SearchResultType.Event)
            ? await SearchEventsAsync(trimmed, ct)
            : Array.Empty<GlobalSearchResult>();

        return new GlobalSearchResults(trimmed, humans, teams, camps, shifts, events);
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchTeamsAsync(
        string query, CancellationToken ct)
    {
        var hits = await teamService.SearchAsync(query, Unlimited, ct);
        return hits
            .Select(t => new GlobalSearchResult(
                Type: SearchResultType.Team,
                Key: t.TeamId,
                SortKey: t.Name,
                Score: t.Score))
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchCampsAsync(
        string query, CancellationToken ct)
    {
        var hits = await campService.SearchAsync(query, Unlimited, ct);
        return hits
            .Select(c => new GlobalSearchResult(
                Type: SearchResultType.Camp,
                Key: c.CampId,
                SortKey: c.Name,
                Score: c.Score))
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchShiftsAsync(
        string query, CancellationToken ct)
    {
        // Hits are rotas (named shift groupings), not individual shifts.
        var hits = await shiftService.SearchAsync(query, Unlimited, ct);
        return hits
            .Select(r => new GlobalSearchResult(
                Type: SearchResultType.Shift,
                Key: r.RotaId,
                SortKey: r.Name,
                Score: r.Score))
            .ToList();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchEventsAsync(
        string query, CancellationToken ct)
    {
        var hits = await eventService.SearchAsync(query, Unlimited, ct);
        return hits
            .Select(e => new GlobalSearchResult(
                Type: SearchResultType.Event,
                Key: e.EventId,
                SortKey: e.Title,
                Score: e.Score))
            .ToList();
    }
}
