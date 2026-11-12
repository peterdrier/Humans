using Humans.Google.Contracts;

namespace Humans.Camps.Services;

internal sealed class CampRoleReader(
    ICampRepository repo,
    ICampRoleCampAccess campAccess,
    IGoogleService googleIdentities,
    GoogleWorkspaceOptions googleOptions) : IApplicationService
{
    public async Task<IReadOnlyList<CampRoleDefinitionInfo>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default)
    {
        var definitions = await repo.ListDefinitionsAsync(includeDeactivated, ct);
        return definitions.Select(CreateCampRoleDefinitionInfo).ToList();
    }

    public async Task<CampRoleDefinitionInfo?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default)
    {
        var definition = await repo.GetDefinitionByIdAsync(id, ct);
        return definition is null ? null : CreateCampRoleDefinitionInfo(definition);
    }

    public async Task<CampRoleDefinitionInfo?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var definition = await repo.GetDefinitionBySlugAsync(slug.Trim(), ct);
        return definition is null ? null : CreateCampRoleDefinitionInfo(definition);
    }

    private static CampRoleDefinitionInfo CreateCampRoleDefinitionInfo(CampRoleDefinition definition) =>
        new(
            definition.Id,
            definition.Name,
            definition.Slug,
            definition.Description,
            definition.SlotCount,
            definition.MinimumRequired,
            definition.SortOrder,
            definition.CreatedAt,
            definition.UpdatedAt,
            definition.DeactivatedAt,
            definition.SpecialRole);

    public async Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        var definitions = await repo.ListDefinitionsAsync(includeDeactivated: false, ct);
        var assignments = await repo.GetAssignmentsForSeasonAsync(campSeasonId, ct);

        var rows = definitions.Select(def =>
        {
            var defAssignments = assignments
                .Where(a => a.CampRoleDefinitionId == def.Id)
                .OrderBy(a => a.AssignedAt)
                .ToList();

            var filled = defAssignments.Select(a =>
                new CampRolesPanelSlot(a.Id, a.CampMemberId, a.CampMember.UserId)).ToList();

            var current = filled.Count;
            var empty = Math.Max(0, def.SlotCount - current);
            var overCapacity = current > def.SlotCount;

            return new CampRolesPanelRow(def, filled, empty, overCapacity, current);
        }).ToList();

        return new CampRolesPanelData(campSeasonId, rows);
    }

    public async Task<CampRoleAssignmentInfo?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var assignment = await repo.GetAssignmentByIdAsync(assignmentId, ct);
        return assignment is null
            ? null
            : new CampRoleAssignmentInfo(
                assignment.Id,
                assignment.CampSeasonId,
                assignment.CampRoleDefinitionId,
                assignment.CampMemberId);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CampDirectoryRoleSummary>>>
        GetDirectoryRoleSummariesAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        var definitions = await repo.ListDefinitionsAsync(includeDeactivated: false, ct);
        var targetEvent = await FindEventAsync(eventSettingsId, ct);
        if (!definitions.Any() || targetEvent is null)
            return new Dictionary<Guid, IReadOnlyList<CampDirectoryRoleSummary>>();

        // Same shape as BuildComplianceMatrixAsync but over all active definitions,
        // with SlotCount as the denominator. Counts cover the event's year; only its seasons are read.
        var seasons = await campAccess.GetCampSeasonsForComplianceAsync(eventSettingsId, ct);
        var counts = await repo.GetAssignmentCountsForYearAsync(targetEvent.Year, ct);
        var countLookup = counts.ToLookup(c => c.CampSeasonId);

        return seasons.ToDictionary(
            season => season.CampSeasonId,
            season => (IReadOnlyList<CampDirectoryRoleSummary>)definitions
                .Select(def => new CampDirectoryRoleSummary(
                    def.Name,
                    countLookup[season.CampSeasonId].FirstOrDefault(r => r.DefinitionId == def.Id).Count,
                    def.SlotCount)
                { SortOrder = def.SortOrder })
                .ToList());
    }

    public async Task<CampComplianceMatrixData?> BuildComplianceMatrixAsync(Guid eventSettingsId, CancellationToken ct = default)
    {
        var targetEvent = await FindEventAsync(eventSettingsId, ct);
        if (targetEvent is null) return null;

        var definitions = (await repo.ListDefinitionsAsync(includeDeactivated: false, ct))
            .Select(CreateCampRoleDefinitionInfo)
            .ToList();

        var seasons = await campAccess.GetCampSeasonsForComplianceAsync(eventSettingsId, ct);
        // Already filtered to active definitions and Active camp members; joins by
        // CampRoleDefinitionId so definition renames cannot empty the matrix.
        var assignments = await repo.GetActiveAssignmentsForYearsAsync([targetEvent.Year], ct);
        var assigneesBySeasonAndDefinition = assignments
            .GroupBy(a => (a.CampSeasonId, a.CampRoleDefinitionId))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Guid>)g.Select(a => a.CampMember.UserId).Distinct().ToList());

        var rows = seasons
            .Where(s => s.Status is CampSeasonStatus.Active or CampSeasonStatus.Full)
            .Select(s => new CampComplianceMatrixRow(
                s.CampName,
                s.CampSlug,
                s.JoinedMemberCount ?? 0,
                s.TargetMemberCount,
                definitions
                    .Select(def => assigneesBySeasonAndDefinition
                        .GetValueOrDefault((s.CampSeasonId, def.Id), []))
                    .ToList()))
            .ToList();

        return new CampComplianceMatrixData(targetEvent, definitions, rows);
    }

    private async Task<CampEventRegistration?> FindEventAsync(Guid eventSettingsId, CancellationToken ct) =>
        (await campAccess.GetSettingsAsync(ct)).Events.FirstOrDefault(e => e.EventSettingsId == eventSettingsId);

    public async Task<IReadOnlyList<CampSpecialRole>> GetMissingSpecialRolesAsync(CancellationToken ct = default)
    {
        var existing = await repo.GetExistingSpecialRolesAsync(ct);
        var existingSet = new HashSet<CampSpecialRole>(existing);
        return Enum.GetValues<CampSpecialRole>()
            .Where(r => r != CampSpecialRole.None && !existingSet.Contains(r))
            .ToList();
    }

    public async Task<CampRoleDrillDownData?> BuildDrillDownAsync(Guid roleDefinitionId, Guid eventSettingsId, CancellationToken ct = default)
    {
        var def = await repo.GetDefinitionByIdAsync(roleDefinitionId, ct);
        var targetEvent = await FindEventAsync(eventSettingsId, ct);
        if (def is null || targetEvent is null) return null;

        var seasons = await campAccess.GetCampSeasonsForComplianceAsync(eventSettingsId, ct);
        var seasonIds = seasons.Select(s => s.CampSeasonId).ToHashSet();
        var assignments = (await repo.GetAssignmentsForDefinitionInYearAsync(def.Id, targetEvent.Year, ct))
            .Where(a => seasonIds.Contains(a.CampSeasonId))
            .ToList();

        var assignmentsBySeason = assignments
            .GroupBy(a => a.CampSeasonId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allUserIds = assignments.Select(a => a.CampMember.UserId).Distinct().ToList();
        var identitiesByUserId = await googleIdentities.GetWorkspaceIdentitiesAsync(allUserIds, ct);

        var rows = seasons
            .Select(s =>
            {
                var assignees = assignmentsBySeason.TryGetValue(s.CampSeasonId, out var list)
                    ? list.Select(a =>
                    {
                        var userId = a.CampMember.UserId;
                        // The member's Google Workspace identity (Google); null without a verified address.
                        var googleEmail = identitiesByUserId.TryGetValue(userId, out var identity) ? identity.Email : null;

                        return new CampRoleDrillDownAssignee(userId, googleEmail, a.AssignedAt);
                    }).ToList()
                    : new List<CampRoleDrillDownAssignee>();

                return new CampRoleDrillDownCampRow(
                    s.CampId, s.CampName, s.CampSlug, s.CampSeasonId, assignees);
            })
            .ToList();

        var info = CreateCampRoleDefinitionInfo(def);
        // Empty Slug => no Google Group (admin hasn't assigned a slug yet).
        var groupEmail = string.IsNullOrWhiteSpace(info.Slug)
            ? null
            : BuildGroupKey(targetEvent.Year, info.Slug);
        return new CampRoleDrillDownData(info, targetEvent, groupEmail, rows);
    }

    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default)
    {
        var requestedKey = string.IsNullOrWhiteSpace(groupKey) ? null : groupKey.Trim();

        // In-scope years: the public year + every open season year. This matches
        // the years users can currently interact with via the UI.
        var settings = await campAccess.GetSettingsAsync(ct);
        var inScopeYears = new HashSet<int>(settings.OpenSeasons) { settings.PublicYear };

        // Empty Slug => the definition does not get a Google Group and is not
        // listed in expected claims. Admins set the slug via the role-edit form
        // when they want a group for this role.
        var activeDefs = (await repo.ListDefinitionsAsync(includeDeactivated: false, ct))
            .Where(d => !string.IsNullOrWhiteSpace(d.Slug))
            .ToList();
        if (activeDefs.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        // GetActiveAssignmentsForYearsAsync already excludes deactivated definitions.
        var assignments = await repo.GetActiveAssignmentsForYearsAsync(inScopeYears, ct);
        var assignmentsBySlugAndYear = assignments
            .Where(a => !string.IsNullOrWhiteSpace(a.Definition.Slug))
            .GroupBy(a => (a.Definition.Slug, a.CampSeason.Year))
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.CampMember.UserId).Distinct().ToArray());

        // Feeds the lead-fallback below.
        var assignmentsByCampSlugYear = assignments
            .Where(a => !string.IsNullOrWhiteSpace(a.Definition.Slug))
            .GroupBy(a => (a.CampSeason.CampId, a.Definition.Slug, a.CampSeason.Year))
            .ToDictionary(g => g.Key, g => g.Select(a => a.CampMember.UserId).Distinct().ToArray());

        var leadsByCampAndYear = assignments
            .Where(a => a.Definition.SpecialRole == CampSpecialRole.Lead)
            .GroupBy(a => (a.CampSeason.CampId, a.CampSeason.Year))
            .ToDictionary(g => g.Key, g => g.Select(a => a.CampMember.UserId).Distinct().ToArray());

        var result = new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in activeDefs)
        {
            foreach (var year in inScopeYears)
            {
                var key = BuildGroupKey(year, def.Slug);
                if (requestedKey is not null
                    && !string.Equals(key, requestedKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var directIds = assignmentsBySlugAndYear.TryGetValue((def.Slug, year), out var ids)
                    ? ids
                    : [];

                // For non-Lead roles: any camp that has a lead but no direct
                // assignee for this role contributes its lead(s) as a stand-in.
                Guid[] fallbackIds = [];
                if (def.SpecialRole != CampSpecialRole.Lead)
                {
                    fallbackIds = leadsByCampAndYear.Keys
                        .Where(k => k.Year == year
                            && !assignmentsByCampSlugYear.ContainsKey((k.CampId, def.Slug, year)))
                        .SelectMany(k => leadsByCampAndYear[k])
                        .Distinct()
                        .ToArray();
                }

                result[key] = directIds.Concat(fallbackIds).Distinct().ToArray();
            }
        }

        return result;
    }

    public string BuildGroupKey(int year, string slug) =>
        $"barrios-{year}-{slug}@{googleOptions.Domain}";
}
