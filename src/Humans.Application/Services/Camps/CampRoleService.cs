using Humans.Application.Configuration;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Application.Services.Camps;

public sealed class CampRoleService(
    ICampRoleRepository repo,
    ICampService campService,
    IUserService userService,
    IUserEmailService userEmailService,
    IAuditLogService auditLog,
    INotificationEmitter notificationEmitter,
    IOptions<GoogleWorkspaceOptions> googleOptions,
    IClock clock,
    ILogger<CampRoleService> logger) : ICampRoleService, IGoogleGroupMembershipSource
{
    private readonly GoogleWorkspaceOptions _googleOptions = googleOptions.Value;

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

    public async Task<CampRoleDefinition> CreateDefinitionAsync(CreateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateMinimumRequired(input.SlotCount, input.MinimumRequired);
        var slug = NormalizeAndValidateSlug(input.Slug);

        if (await repo.DefinitionNameExistsAsync(input.Name, excludingId: null, ct))
            throw new InvalidOperationException($"A camp role definition named '{input.Name}' already exists.");

        if (await repo.DefinitionSlugExistsAsync(slug, excludingId: null, ct))
            throw new InvalidOperationException($"A camp role definition with slug '{slug}' already exists.");

        var now = clock.GetCurrentInstant();
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Slug = slug,
            Description = input.Description,
            SlotCount = input.SlotCount,
            MinimumRequired = input.MinimumRequired,
            SortOrder = input.SortOrder,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repo.AddDefinitionAsync(def, ct); // SaveChangesAsync first
        await auditLog.LogAsync(
            AuditAction.CampRoleDefinitionCreated,
            nameof(CampRoleDefinition),
            def.Id,
            $"Created camp role definition '{def.Name}'.",
            actorUserId);

        return def;
    }

    private static void ValidateMinimumRequired(int slotCount, int minimumRequired)
    {
        if (minimumRequired < 0 || minimumRequired > slotCount)
            throw new ArgumentException(
                $"MinimumRequired must satisfy 0 ≤ MinimumRequired ≤ SlotCount (got SlotCount={slotCount}, MinimumRequired={minimumRequired}).",
                nameof(minimumRequired));
    }

    private static string NormalizeAndValidateSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug is required.", nameof(slug));
        var normalized = slug.Trim().ToLowerInvariant();
        if (!Helpers.SlugHelper.IsValidKebabSlug(normalized))
            throw new ArgumentException(
                "Slug must be kebab-case (lowercase letters, digits, and hyphens; no leading, trailing, or consecutive hyphens; max 60 chars).",
                nameof(slug));
        // "create" would route to CampAdminController.CreateRole instead of RolesDrillDown.
        if (string.Equals(normalized, "create", StringComparison.Ordinal))
            throw new InvalidOperationException("\"create\" is reserved and cannot be used as a camp role slug.");
        return normalized;
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
            definition.DeactivatedAt);

    private static CampRoleAssignmentInfo CreateCampRoleAssignmentInfo(CampRoleAssignment assignment) =>
        new(
            assignment.Id,
            assignment.CampSeasonId,
            assignment.CampRoleDefinitionId,
            assignment.CampMemberId);

    public async Task<UpdateCampRoleDefinitionResult> UpdateDefinitionAsync(Guid id, UpdateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateMinimumRequired(input.SlotCount, input.MinimumRequired);
        var slug = NormalizeAndValidateSlug(input.Slug);

        if (await repo.DefinitionNameExistsAsync(input.Name, excludingId: id, ct))
            throw new InvalidOperationException($"A camp role definition named '{input.Name}' already exists.");

        if (await repo.DefinitionSlugExistsAsync(slug, excludingId: id, ct))
            throw new InvalidOperationException($"A camp role definition with slug '{slug}' already exists.");

        var now = clock.GetCurrentInstant();
        var updated = await repo.UpdateDefinitionAsync(id, def =>
        {
            def.Name = input.Name;
            def.Slug = slug;
            def.Description = input.Description;
            def.SlotCount = input.SlotCount;
            def.MinimumRequired = input.MinimumRequired;
            def.SortOrder = input.SortOrder;
            def.UpdatedAt = now;
        }, ct);

        if (!updated) return UpdateCampRoleDefinitionResult.NotFound;

        await auditLog.LogAsync(
            AuditAction.CampRoleDefinitionUpdated,
            nameof(CampRoleDefinition),
            id,
            $"Updated camp role definition '{input.Name}'.",
            actorUserId);

        return UpdateCampRoleDefinitionResult.Updated(input.Name);
    }

    public async Task<bool> DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var updated = await repo.UpdateDefinitionAsync(id, def =>
        {
            if (def.DeactivatedAt is null)
                def.DeactivatedAt = now;
            def.UpdatedAt = now;
        }, ct);
        if (!updated) return false;

        await auditLog.LogAsync(
            AuditAction.CampRoleDefinitionDeactivated,
            nameof(CampRoleDefinition), id,
            "Deactivated camp role definition.", actorUserId);
        return true;
    }

    public async Task<bool> ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var updated = await repo.UpdateDefinitionAsync(id, def =>
        {
            def.DeactivatedAt = null;
            def.UpdatedAt = now;
        }, ct);
        if (!updated) return false;

        await auditLog.LogAsync(
            AuditAction.CampRoleDefinitionReactivated,
            nameof(CampRoleDefinition), id,
            "Reactivated camp role definition.", actorUserId);
        return true;
    }

    public async Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default)
    {
        var definitions = await repo.ListDefinitionsAsync(includeDeactivated: false, ct);
        var assignments = await repo.GetAssignmentsForSeasonAsync(campSeasonId, ct);

        var memberUserIds = assignments.Select(a => a.CampMember.UserId).Distinct().ToList();
        IReadOnlyDictionary<Guid, UserInfo> users = memberUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(memberUserIds, ct);

        var rows = definitions.Select(def =>
        {
            var defAssignments = assignments
                .Where(a => a.CampRoleDefinitionId == def.Id)
                .OrderBy(a => a.AssignedAt)
                .ToList();

            var filled = defAssignments.Select(a =>
            {
                var displayName = users.TryGetValue(a.CampMember.UserId, out var u)
                    ? u.BurnerName
                    : "(unknown)";
                return new CampRolesPanelSlot(a.Id, a.CampMemberId, a.CampMember.UserId, displayName);
            }).ToList();

            var current = filled.Count;
            var empty = Math.Max(0, def.SlotCount - current);
            var overCapacity = current > def.SlotCount;

            return new CampRolesPanelRow(def, filled, empty, overCapacity, current);
        }).ToList();

        return new CampRolesPanelData(campSeasonId, rows);
    }

    public async Task<AssignCampRoleOutcome> AssignAsync(
        Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct = default)
    {
        var def = await repo.GetDefinitionByIdAsync(roleDefinitionId, ct);
        if (def is null) return AssignCampRoleOutcome.RoleNotFound;
        if (def.DeactivatedAt is not null) return AssignCampRoleOutcome.RoleDeactivated;

        var memberLookup = await campService.GetCampMemberStatusAsync(campMemberId, ct);
        if (memberLookup is null) return AssignCampRoleOutcome.MemberNotFound;
        if (memberLookup.CampSeasonId != campSeasonId) return AssignCampRoleOutcome.MemberSeasonMismatch;
        if (memberLookup.Status != CampMemberStatus.Active) return AssignCampRoleOutcome.MemberNotActive;

        if (await repo.AssignmentExistsAsync(campSeasonId, roleDefinitionId, campMemberId, ct))
            return AssignCampRoleOutcome.AlreadyHoldsRole;

        var existingCount = await repo.CountAssignmentsForSeasonAndDefinitionAsync(campSeasonId, roleDefinitionId, ct);
        if (existingCount >= def.SlotCount)
            return AssignCampRoleOutcome.SlotCapReached;

        var now = clock.GetCurrentInstant();
        var assignment = new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            CampRoleDefinitionId = roleDefinitionId,
            CampMemberId = campMemberId,
            AssignedAt = now,
            AssignedByUserId = actorUserId,
        };

        var inserted = await repo.AddAssignmentAsync(assignment, ct);
        if (!inserted)
        {
            // Repo translated the unique-index race.
            return AssignCampRoleOutcome.AlreadyHoldsRole;
        }

        await auditLog.LogAsync(
            AuditAction.CampRoleAssigned,
            nameof(CampRoleAssignment),
            assignment.Id,
            $"Assigned role '{def.Name}' to member.",
            actorUserId,
            relatedEntityId: campMemberId, relatedEntityType: nameof(CampMember));

        try
        {
            await notificationEmitter.SendAsync(
                source: NotificationSource.CampRoleAssigned,
                notificationClass: NotificationClass.Informational,
                priority: NotificationPriority.Normal,
                title: $"You were assigned the {def.Name} role.",
                recipientUserIds: [memberLookup.UserId],
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification failed for CampRoleAssigned (assignment {AssignmentId}).", assignment.Id);
        }

        return AssignCampRoleOutcome.Assigned;
    }

    public async Task<CampRoleAssignmentInfo?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default)
    {
        var assignment = await repo.GetAssignmentByIdAsync(assignmentId, ct);
        return assignment is null ? null : CreateCampRoleAssignmentInfo(assignment);
    }

    public async Task<bool> UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct = default)
    {
        var assignment = await repo.GetAssignmentByIdAsync(assignmentId, ct);
        if (assignment is null) return false;

        var deleted = await repo.DeleteAssignmentAsync(assignmentId, ct);
        if (!deleted) return false;

        await auditLog.LogAsync(
            AuditAction.CampRoleUnassigned,
            nameof(CampRoleAssignment),
            assignmentId,
            "Unassigned camp role.",
            actorUserId,
            relatedEntityId: assignment.CampMemberId,
            relatedEntityType: nameof(CampMember));

        return true;
    }

    public async Task<int> RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct = default)
    {
        var deleted = await repo.DeleteAllForMemberAsync(campMemberId, ct);
        if (deleted > 0)
        {
            await auditLog.LogAsync(
                AuditAction.CampRoleUnassigned,
                nameof(CampMember), campMemberId,
                $"Cascade-removed {deleted} role assignment(s) for camp member.",
                actorUserId);
        }
        return deleted;
    }

    public async Task<CampRoleComplianceReport> GetComplianceReportAsync(int year, CancellationToken ct = default)
    {
        var requiredDefs = (await repo.ListDefinitionsAsync(includeDeactivated: false, ct))
            .Where(d => d.MinimumRequired > 0)
            .ToList();

        var camps = await campService.GetCampSeasonsForComplianceAsync(year, ct);
        var counts = await repo.GetAssignmentCountsForYearAsync(year, ct);
        var countLookup = counts.ToLookup(c => c.CampSeasonId);

        var rows = camps.Select(c =>
        {
            var roles = requiredDefs.Select(def =>
            {
                var filled = countLookup[c.CampSeasonId].FirstOrDefault(r => r.DefinitionId == def.Id).Count;
                return new CampRoleComplianceRoleRow(def.Id, def.Name, def.MinimumRequired, filled, filled >= def.MinimumRequired);
            }).ToList();

            var allMet = roles.All(r => r.IsMet);
            return new CampRoleComplianceCampRow(c.CampId, c.CampName, c.CampSlug, c.CampSeasonId, roles, allMet);
        }).ToList();

        return new CampRoleComplianceReport(year, rows);
    }

    public async Task<CampRoleDrillDownData?> BuildDrillDownAsync(Guid roleDefinitionId, int year, CancellationToken ct = default)
    {
        var def = await repo.GetDefinitionByIdAsync(roleDefinitionId, ct);
        if (def is null) return null;

        var seasons = await campService.GetCampSeasonsForComplianceAsync(year, ct);
        var assignments = await repo.GetAssignmentsForDefinitionInYearAsync(def.Id, year, ct);

        var assignmentsBySeason = assignments
            .GroupBy(a => a.CampSeasonId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allUserIds = assignments.Select(a => a.CampMember.UserId).Distinct().ToList();
        var emailsByUserId = allUserIds.Count == 0
            ? new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>()
            : await userEmailService.GetEntitiesByUserIdsAsync(allUserIds, ct);

        var rows = seasons
            .Select(s =>
            {
                var assignees = assignmentsBySeason.TryGetValue(s.CampSeasonId, out var list)
                    ? list.Select(a =>
                    {
                        var userId = a.CampMember.UserId;
                        var googleEmail = TryGetGoogleEmail(userId, emailsByUserId);
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
            : BuildGroupKey(year, info.Slug);
        return new CampRoleDrillDownData(info, year, groupEmail, rows);
    }

    // ==========================================================================
    // IGoogleGroupMembershipSource
    // ==========================================================================

    /// <inheritdoc />
    public async Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default)
    {
        var requestedKey = string.IsNullOrWhiteSpace(groupKey) ? null : groupKey.Trim();

        // In-scope years: the public year + every open season year. This matches
        // the years users can currently interact with via the UI.
        var settings = await campService.GetSettingsAsync(ct);
        var inScopeYears = new HashSet<int>(settings.OpenSeasons) { settings.PublicYear };
        if (inScopeYears.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        // Empty Slug => the definition does not get a Google Group and is not
        // listed in expected claims. Admins set the slug via the role-edit form
        // when they want a group for this role.
        var activeDefs = (await repo.ListDefinitionsAsync(includeDeactivated: false, ct))
            .Where(d => !string.IsNullOrWhiteSpace(d.Slug))
            .ToList();
        if (activeDefs.Count == 0)
            return new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);

        // Pull assignments for every in-scope year in one shot. Filters out
        // deactivated definitions at the repo level.
        var assignments = await repo.GetActiveAssignmentsForYearsAsync(inScopeYears, ct);
        var assignmentsBySlugAndYear = assignments
            .Where(a => !string.IsNullOrWhiteSpace(a.Definition.Slug))
            .GroupBy(a => (a.Definition.Slug, Year: a.CampSeason.Year))
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.CampMember.UserId).Distinct().ToArray());

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
                var userIds = assignmentsBySlugAndYear.TryGetValue((def.Slug, year), out var ids)
                    ? ids
                    : [];
                result[key] = userIds;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the deterministic group key for a (role-definition slug, season year) pair:
    /// <c>barrios-{year}-{slug}@{domain}</c>.
    /// </summary>
    public string BuildGroupKey(int year, string slug) =>
        $"barrios-{year}-{slug}@{_googleOptions.Domain}";

    private static string? TryGetGoogleEmail(
        Guid userId,
        IReadOnlyDictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>> emailsByUserId)
    {
        if (!emailsByUserId.TryGetValue(userId, out var emails))
            return null;
        return emails
            .Where(e => e.IsVerified && e.IsGoogle)
            .Select(e => e.Email)
            .FirstOrDefault()
            ?? emails
                .Where(e => e.IsVerified)
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.Email)
                .FirstOrDefault();
    }
}
