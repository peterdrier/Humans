using Humans.Application.DTOs;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Search;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Search;

/// <summary>
/// Application-layer implementation of <see cref="ISearchService"/>. The
/// per-section query lives in each section's own service
/// (<c>ITeamService.SearchAsync</c>, <c>ICampService.SearchAsync</c>,
/// <c>IShiftManagementService.SearchAsync</c>,
/// <c>IProfileService.SearchProfilesAsync</c>) — those services own their
/// tables and do the case-insensitive Postgres ILike filter at the DB layer
/// per <c>memory/feedback_ef_ilike_not_toupper.md</c>. This orchestrator
/// merges the per-section hits, computes a uniform score, applies the
/// optional group filter, and adds the surviving cross-modal pull-ins
/// (people → teams + camps they lead).
///
/// <para>
/// Authorization gate: <c>includeAdmin</c> is set by the controller only
/// after the caller has been verified as an admin-shaped role. It maps to
/// <see cref="PersonSearchFields.AdminAll"/> versus
/// <see cref="PersonSearchFields.PublicAll"/> for humans, and to a
/// hidden-teams-included filter for the team query.
/// </para>
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly ICampService _campService;
    private readonly IShiftManagementService _shiftService;

    // Score band — keep these gentle so a multi-field match boosts above a
    // single-field match without dwarfing it. Title-equals beats
    // title-startswith beats title-contains beats body-contains.
    private const int ScoreExactName = 100;
    private const int ScorePrefixName = 80;
    private const int ScoreContainsName = 60;
    private const int ScoreSlugMatch = 70;
    private const int ScoreContainsBody = 30;
    private const int ScoreMultiFieldBoost = 15;

    // Lower-priority pull-ins for relational hits (camps a person leads,
    // teams a person belongs to, rotas under a matched team).
    private const int ScoreRelationalHit = 20;

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
        SearchResultType? filter = null,
        bool includeAdmin = false,
        int perTypeLimit = 10,
        CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length < 2)
        {
            return new GlobalSearchResults(trimmed, Array.Empty<GlobalSearchResult>(), 0, 0, 0, 0);
        }

        // ------------------------------------------------------------------
        // Direct hits per section. Each section's own service runs the
        // ILike DB query and returns the matched entities; we score and
        // render here.
        // ------------------------------------------------------------------
        var humanHits = await SearchHumansAsync(trimmed, includeAdmin, perTypeLimit, ct);
        var (teamHits, matchedTeamIds) = await SearchTeamsAsync(trimmed, includeAdmin, perTypeLimit, ct);
        var campHits = await SearchCampsAsync(trimmed, perTypeLimit, ct);
        var shiftHits = await SearchShiftsAsync(trimmed, perTypeLimit, ct);

        // ------------------------------------------------------------------
        // Cross-modal relational hits — typing a person's name surfaces
        // their teams and camps they lead; typing a team's name surfaces
        // its rotas. Camps and rotas have no relational link in the data
        // model, so there is no camp→shift expansion.
        // ------------------------------------------------------------------
        var relationalHits = await BuildRelationalHitsAsync(humanHits, teamHits, matchedTeamIds, ct);

        // De-duplicate: a direct hit on a team should NOT be re-listed as a
        // relational pull-in if it already appears in teamHits.
        var directKeys = humanHits.Concat(teamHits).Concat(campHits).Concat(shiftHits)
            .Select(r => (r.Type, r.Url))
            .ToHashSet();
        var dedupedRelational = relationalHits
            .Where(r => directKeys.Add((r.Type, r.Url)))
            .ToList();

        // Counts (post-dedup) drive the "See all 47 humans" footer link.
        var humanCount = humanHits.Count + dedupedRelational.Count(r => r.Type == SearchResultType.Human);
        var teamCount = teamHits.Count + dedupedRelational.Count(r => r.Type == SearchResultType.Team);
        var campCount = campHits.Count + dedupedRelational.Count(r => r.Type == SearchResultType.Camp);
        var shiftCount = shiftHits.Count + dedupedRelational.Count(r => r.Type == SearchResultType.Shift);

        // Apply the per-type group filter chip if the controller passed one.
        // Filters are presentation-layer; we still computed counts from the
        // unfiltered data so chip labels stay honest ("Humans (4)" even when
        // the active filter is Teams).
        var unioned = humanHits.Concat(teamHits).Concat(campHits).Concat(shiftHits)
            .Concat(dedupedRelational);
        if (filter.HasValue)
        {
            unioned = unioned.Where(r => r.Type == filter.Value);
        }

        var ranked = unioned
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GlobalSearchResults(trimmed, ranked, humanCount, teamCount, campCount, shiftCount);
    }

    // ======================================================================
    // Per-section direct searches
    // ======================================================================

    private async Task<List<GlobalSearchResult>> SearchHumansAsync(
        string query, bool includeAdmin, int limit, CancellationToken ct)
    {
        var fields = includeAdmin ? PersonSearchFields.AdminAll : PersonSearchFields.PublicAll;
        var hits = await _profileService.SearchProfilesAsync(query, fields, limit, ct);

        return hits
            .Select(h => new GlobalSearchResult(
                Type: SearchResultType.Human,
                Title: h.BurnerName,
                Subtitle: ComposeHumanSubtitle(h),
                Url: $"/Profile/{h.UserId}",
                Score: ScoreFromMatchField(h.BurnerName, query, h.MatchField),
                UserId: h.UserId,
                MatchField: h.MatchField))
            .ToList();
    }

    private async Task<(List<GlobalSearchResult> Hits, IReadOnlyList<Guid> MatchedTeamIds)>
        SearchTeamsAsync(string query, bool includeAdmin, int limit, CancellationToken ct)
    {
        // ITeamService.SearchAsync runs the ILike filter at the DB layer
        // (design-rules §6 — query lives in the owning section). Hidden
        // teams are filtered there too; admins pass includeHidden=true.
        var hits = await _teamService.SearchAsync(query, includeAdmin, limit, ct);

        var scored = hits
            .Select(t =>
            {
                var score = ScoreEntityMatch(query, t.Name, t.Slug, t.Description, out var matchField);
                return (Hit: t, Score: score, MatchField: matchField);
            })
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .ToList();

        var rendered = scored
            .Select(s => new GlobalSearchResult(
                Type: SearchResultType.Team,
                Title: s.Hit.Name,
                Subtitle: ComposeSnippet(query, s.Hit.Description) ?? s.Hit.Slug,
                Url: $"/Teams/{s.Hit.Slug}",
                Score: s.Score,
                MatchField: s.MatchField))
            .ToList();

        var ids = scored.Select(s => s.Hit.Id).ToList();
        return (rendered, ids);
    }

    private async Task<List<GlobalSearchResult>> SearchCampsAsync(
        string query, int limit, CancellationToken ct)
    {
        // ICampService.SearchAsync owns the public-year resolution and the
        // ILike DB filter; we just score and render.
        var hits = await _campService.SearchAsync(query, limit, ct);

        return hits
            .Select(c =>
            {
                var score = ScoreEntityMatch(query, c.Name, c.Slug, c.Blurb, out var matchField);
                return (Hit: c, Score: score, MatchField: matchField);
            })
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .Select(s => new GlobalSearchResult(
                Type: SearchResultType.Camp,
                Title: s.Hit.Name,
                Subtitle: ComposeSnippet(query, s.Hit.Blurb) ?? s.Hit.Slug,
                Url: $"/Camps/{s.Hit.Slug}",
                Score: s.Score,
                MatchField: s.MatchField))
            .ToList();
    }

    private async Task<List<GlobalSearchResult>> SearchShiftsAsync(
        string query, int limit, CancellationToken ct)
    {
        // IShiftManagementService.SearchAsync runs the ILike filter at the
        // DB layer and stitches the owning team's display name. The "shift"
        // search hit is a Rota (named, role-shaped grouping of shifts) —
        // an individual Shift row is a date+time slot with no
        // human-readable title to match against.
        var hits = await _shiftService.SearchAsync(query, limit, ct);

        return hits
            .Select(r =>
            {
                var score = ScoreEntityMatch(query, r.Name, slug: null, r.Description, out var matchField);
                return (Hit: r, Score: score, MatchField: matchField);
            })
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .Select(s => new GlobalSearchResult(
                Type: SearchResultType.Shift,
                Title: s.Hit.Name,
                Subtitle: ComposeSnippet(query, s.Hit.Description) ?? s.Hit.TeamName,
                Url: $"/Shifts?departmentId={s.Hit.TeamId}",
                Score: s.Score,
                MatchField: s.MatchField))
            .ToList();
    }

    // ======================================================================
    // Cross-modal relational hits
    // ======================================================================

    private async Task<List<GlobalSearchResult>> BuildRelationalHitsAsync(
        IReadOnlyList<GlobalSearchResult> humanHits,
        IReadOnlyList<GlobalSearchResult> teamHits,
        IReadOnlyList<Guid> matchedTeamIds,
        CancellationToken ct)
    {
        var relational = new List<GlobalSearchResult>();

        // Person → teams they're members of. Resolve team display data
        // from the active-teams snapshot rather than the cross-domain
        // membership-side nav (per design-rules §6c the section keeps the
        // FK-only shape — we look up by id).
        IReadOnlyDictionary<Guid, Team>? activeTeamsById = null;
        if (humanHits.Count > 0)
        {
            var allTeams = await _teamService.GetAllTeamsAsync(ct);
            activeTeamsById = allTeams.ToDictionary(t => t.Id);
        }

        if (activeTeamsById is not null)
        {
            foreach (var human in humanHits.Where(h => h.UserId.HasValue))
            {
                var memberships = await _teamService.GetUserTeamsAsync(human.UserId!.Value, ct);
                foreach (var membership in memberships)
                {
                    if (membership.LeftAt is not null) continue;
                    if (!activeTeamsById.TryGetValue(membership.TeamId, out var t)) continue;
                    if (!t.IsActive || t.IsHidden) continue;
                    relational.Add(new GlobalSearchResult(
                        Type: SearchResultType.Team,
                        Title: t.Name,
                        Subtitle: t.Slug,
                        Url: $"/Teams/{t.Slug}",
                        Score: ScoreRelationalHit,
                        RelationContext: $"Member: {human.Title}"));
                }
            }
        }

        // Person → camps they lead. Load camps-with-leads for the public
        // year; the lead row carries UserId only (per design-rules §6 the
        // section avoids cross-domain navs), so we match by id.
        if (humanHits.Count > 0)
        {
            var settings = await _campService.GetSettingsAsync(ct);
            var campYear = settings.PublicYear;
            var campsWithLeads = await _campService.GetCampsWithLeadsForYearAsync(
                campYear, statusFilter: null, ct);

            foreach (var human in humanHits.Where(h => h.UserId.HasValue))
            {
                var ledCamps = campsWithLeads.Where(c =>
                    c.Leads.Any(l => l.UserId == human.UserId!.Value && l.LeftAt == null));
                foreach (var camp in ledCamps)
                {
                    var name = camp.Seasons.FirstOrDefault(s => s.Year == campYear)?.Name ?? camp.Slug;
                    relational.Add(new GlobalSearchResult(
                        Type: SearchResultType.Camp,
                        Title: name,
                        Subtitle: camp.Slug,
                        Url: $"/Camps/{camp.Slug}",
                        Score: ScoreRelationalHit,
                        RelationContext: $"Lead: {human.Title}"));
                }
            }
        }

        // Team → rotas. When a team matches by name/description, surface
        // its volunteer-visible rotas as lower-ranked relational hits.
        var settingsForShifts = await _shiftService.GetActiveAsync();
        if (settingsForShifts is not null && matchedTeamIds.Count > 0)
        {
            for (var i = 0; i < matchedTeamIds.Count; i++)
            {
                var teamId = matchedTeamIds[i];
                var teamHit = teamHits[i];
                var rotas = await _shiftService.GetRotasByDepartmentAsync(teamId, settingsForShifts.Id);
                foreach (var rota in rotas.Where(r => r.IsVisibleToVolunteers))
                {
                    relational.Add(new GlobalSearchResult(
                        Type: SearchResultType.Shift,
                        Title: rota.Name,
                        Subtitle: teamHit.Title,
                        Url: $"/Shifts?departmentId={rota.TeamId}",
                        Score: ScoreRelationalHit,
                        RelationContext: $"Shift in {teamHit.Title}"));
                }
            }
        }

        return relational;
    }

    // ======================================================================
    // Scoring + snippet helpers
    // ======================================================================

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

    private static int ScoreFromMatchField(string burnerName, string query, string? matchField)
    {
        // Map the Profile section's MatchField label back into our scoring
        // bands. Profile's matcher already prioritized name; we replicate
        // here to keep humans on the same scale as teams/camps/shifts.
        var nameScore = ScoreNameField(burnerName, query);
        if (nameScore > 0) return nameScore;

        // Non-name match in PersonSearchMatcher (Bio, City, CV, etc).
        return string.IsNullOrEmpty(matchField) ? ScoreContainsBody : ScoreContainsBody + ScoreMultiFieldBoost / 2;
    }

    private static string? ComposeHumanSubtitle(HumanSearchResult h)
    {
        // Avoid the "Type: Name" shape which would be redundant with the
        // type badge in the view. Surface the matched bucket / snippet /
        // email instead, in priority order.
        if (!string.IsNullOrEmpty(h.MatchSnippet)) return h.MatchSnippet;
        if (!string.IsNullOrEmpty(h.MatchedEmail)) return h.MatchedEmail;
        if (!string.IsNullOrEmpty(h.MatchField)) return h.MatchField;
        return null;
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
