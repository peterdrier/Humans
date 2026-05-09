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
/// Application-layer implementation of <see cref="ISearchService"/>. Goes
/// through public per-section service interfaces only — never reaches into
/// another section's repository, store, or DbContext (design-rules §6 +
/// §2c). Each section's data is already cached / hydrated by its owning
/// service, so this orchestrator only loads existing snapshots and runs
/// in-memory matching at ~500-user scale (per <c>CLAUDE.md</c>: "Prefer
/// in-memory caching over query optimization").
///
/// <para>
/// In-memory matching also satisfies the
/// <c>memory/feedback_ef_ilike_not_toupper.md</c> rule trivially — there
/// are no Postgres LIKE queries to write here. The rule applies to
/// repository code that does case-insensitive Postgres queries; an
/// orchestrator that filters already-loaded objects in C# does not need
/// it.
/// </para>
///
/// <para>
/// Authorization gate: <c>includeAdmin</c> is set by the controller
/// only after the caller has been verified as an admin-shaped role.
/// Forwards to <see cref="PersonSearchFields.AdminAll"/> versus
/// <see cref="PersonSearchFields.PublicAll"/>.
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
        // Direct hits per section. Each section's own data ownership is
        // preserved — we go through the public interface, not the repo.
        // ------------------------------------------------------------------
        var humanHits = await SearchHumansAsync(trimmed, includeAdmin, perTypeLimit, ct);
        var (teamHits, matchedTeamIds) = await SearchTeamsAsync(trimmed, includeAdmin, perTypeLimit, ct);
        var (campHits, matchedCampSlugs) = await SearchCampsAsync(trimmed, perTypeLimit, ct);
        var shiftHits = await SearchShiftsAsync(trimmed, perTypeLimit, ct);

        // ------------------------------------------------------------------
        // Cross-modal relational hits — typing a person's name surfaces
        // their teams and camps; typing a team's name surfaces its rotas;
        // typing a camp's name surfaces its leads (the data model has no
        // direct camp→rota link, so leads are the closest substitute).
        // ------------------------------------------------------------------
        var relationalHits = await BuildRelationalHitsAsync(
            humanHits, teamHits, matchedTeamIds, campHits, matchedCampSlugs, ct);

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
        // GetAllTeamsAsync returns active teams only — exactly what a public
        // search should surface. Hidden teams stay out of non-admin results
        // (matches the team directory's three-bucket model: Departments /
        // System / Hidden); admins see hidden teams too.
        var teams = await _teamService.GetAllTeamsAsync(ct);

        var matches = new List<(Team Entity, int Score, string MatchField)>();
        foreach (var team in teams)
        {
            if (team.IsHidden && !includeAdmin) continue;
            var score = ScoreEntityMatch(query, team.Name, team.Slug, team.Description, out var matchField);
            if (score > 0)
            {
                matches.Add((team, score, matchField));
            }
        }

        var ordered = matches
            .OrderByDescending(m => m.Score)
            .Take(limit)
            .ToList();

        var hits = ordered
            .Select(m => new GlobalSearchResult(
                Type: SearchResultType.Team,
                Title: m.Entity.Name,
                Subtitle: ComposeSnippet(query, m.Entity.Description) ?? m.Entity.Slug,
                Url: $"/Teams/{m.Entity.Slug}",
                Score: m.Score,
                MatchField: m.MatchField))
            .ToList();

        var ids = ordered.Select(m => m.Entity.Id).ToList();
        return (hits, ids);
    }

    private async Task<(List<GlobalSearchResult> Hits, IReadOnlyList<string> MatchedSlugs)>
        SearchCampsAsync(string query, int limit, CancellationToken ct)
    {
        // Pull camps for the public year — this is the population a viewer
        // would see in the camp directory, so the search surface matches.
        var settings = await _campService.GetSettingsAsync(ct);
        var camps = await _campService.GetCampsWithLeadsForYearAsync(
            settings.PublicYear, statusFilter: null, ct);

        var year = settings.PublicYear;

        var matches = new List<(Camp Camp, string Name, string? Blurb, int Score, string MatchField)>();
        foreach (var camp in camps)
        {
            var season = camp.Seasons.FirstOrDefault(s => s.Year == year);
            var name = season?.Name ?? camp.Slug;
            var blurb = season?.BlurbShort;
            var score = ScoreEntityMatch(query, name, camp.Slug, blurb, out var matchField);
            if (score > 0)
            {
                matches.Add((camp, name, blurb, score, matchField));
            }
        }

        var ordered = matches
            .OrderByDescending(m => m.Score)
            .Take(limit)
            .ToList();

        var hits = ordered
            .Select(m => new GlobalSearchResult(
                Type: SearchResultType.Camp,
                Title: m.Name,
                Subtitle: ComposeSnippet(query, m.Blurb) ?? m.Camp.Slug,
                Url: $"/Camps/{m.Camp.Slug}",
                Score: m.Score,
                MatchField: m.MatchField))
            .ToList();

        var slugs = ordered.Select(m => m.Camp.Slug).ToList();
        return (hits, slugs);
    }

    private async Task<List<GlobalSearchResult>> SearchShiftsAsync(
        string query, int limit, CancellationToken ct)
    {
        // The "shift" search hit is a Rota (named, role-shaped grouping of
        // shifts) — an individual Shift row is a date+time slot with no
        // human-readable title to match against. Iterate over departments
        // that own rotas and match Rota.Name + Description.
        var settings = await _shiftService.GetActiveAsync();
        if (settings is null) return new List<GlobalSearchResult>();

        var departments = await _shiftService.GetDepartmentsWithRotasAsync(settings.Id);

        var matches = new List<(Rota Rota, string TeamName, int Score, string MatchField)>();
        foreach (var (teamId, teamName) in departments)
        {
            var rotas = await _shiftService.GetRotasByDepartmentAsync(teamId, settings.Id);
            foreach (var rota in rotas.Where(r => r.IsVisibleToVolunteers))
            {
                var score = ScoreEntityMatch(query, rota.Name, slug: null, rota.Description, out var matchField);
                if (score > 0)
                {
                    matches.Add((rota, teamName, score, matchField));
                }
            }
        }

        return matches
            .OrderByDescending(m => m.Score)
            .Take(limit)
            .Select(m => new GlobalSearchResult(
                Type: SearchResultType.Shift,
                Title: m.Rota.Name,
                Subtitle: ComposeSnippet(query, m.Rota.Description) ?? m.TeamName,
                Url: $"/Shifts?departmentId={m.Rota.TeamId}",
                Score: m.Score,
                MatchField: m.MatchField))
            .ToList();
    }

    // ======================================================================
    // Cross-modal relational hits
    // ======================================================================

    private async Task<List<GlobalSearchResult>> BuildRelationalHitsAsync(
        IReadOnlyList<GlobalSearchResult> humanHits,
        IReadOnlyList<GlobalSearchResult> teamHits,
        IReadOnlyList<Guid> matchedTeamIds,
        IReadOnlyList<GlobalSearchResult> campHits,
        IReadOnlyList<string> matchedCampSlugs,
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
        IReadOnlyList<Camp>? campsWithLeads = null;
        int campYear = 0;
        if (humanHits.Count > 0 || campHits.Count > 0)
        {
            var settings = await _campService.GetSettingsAsync(ct);
            campYear = settings.PublicYear;
            campsWithLeads = await _campService.GetCampsWithLeadsForYearAsync(
                campYear, statusFilter: null, ct);
        }

        if (campsWithLeads is not null)
        {
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

        // Camp → leads (humans). The data model has no camp→rota
        // relationship (rotas belong to Teams, not Camps), so the spec's
        // "typing a camp name → that camp's shifts" surfaces as the camp's
        // human leads instead — the actual people associated with that
        // camp. Documented in PR description.
        if (campsWithLeads is not null && matchedCampSlugs.Count > 0)
        {
            for (var i = 0; i < matchedCampSlugs.Count; i++)
            {
                var slug = matchedCampSlugs[i];
                var campHit = campHits[i];
                var camp = campsWithLeads.FirstOrDefault(c =>
                    string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
                if (camp is null) continue;
                foreach (var lead in camp.Leads.Where(l => l.LeftAt == null))
                {
                    relational.Add(new GlobalSearchResult(
                        Type: SearchResultType.Human,
                        Title: $"Lead at {campHit.Title}",
                        Subtitle: null,
                        Url: $"/Profile/{lead.UserId}",
                        Score: ScoreRelationalHit,
                        UserId: lead.UserId,
                        RelationContext: $"Lead at {campHit.Title}"));
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
