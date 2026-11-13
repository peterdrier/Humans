using System.Globalization;
using System.Resources;
using Humans.Google.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampRoleService(
    ICampRepository repo,
    ICampRoleCampAccess campAccess,
    IGoogleService googleIdentities,
    IUserService userService,
    IAuditLogService auditLog,
    INotificationEmitter notificationEmitter,
    IOptions<GoogleWorkspaceOptions> googleOptions,
    IClock clock,
    ILogger<CampRoleService> logger) : ICampRoleService, ICampRoleSeeding, IGoogleGroupMembershipSource
{
    private readonly CampRoleReader roleReader = new(repo, campAccess, googleIdentities, googleOptions.Value);
    private static readonly ResourceManager NoticeResources = new(typeof(CampsResource));

    public Task<IReadOnlyList<CampRoleDefinitionInfo>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default) =>
        roleReader.ListDefinitionsAsync(includeDeactivated, ct);

    public Task<CampRoleDefinitionInfo?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default) =>
        roleReader.GetDefinitionByIdAsync(id, ct);

    public Task<CampRoleDefinitionInfo?> GetDefinitionBySlugAsync(string slug, CancellationToken ct = default) =>
        roleReader.GetDefinitionBySlugAsync(slug, ct);

    public async Task<CampAdminMutationResult> CreateDefinitionAsync(CampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
    {
        if (ValidateDefinitionInput(input.SlotCount, input.MinimumRequired, input.Slug) is { } error)
            return new(false, error);
        var slug = input.Slug.Trim().ToLowerInvariant();

        if (await repo.DefinitionNameExistsAsync(input.Name, excludingId: null, ct))
            return new(false, $"A camp role definition named '{input.Name}' already exists.");

        if (await repo.DefinitionSlugExistsAsync(slug, excludingId: null, ct))
            return new(false, $"A camp role definition with slug '{slug}' already exists.");

        var now = clock.GetCurrentInstant();
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApplyDefinitionFields(def, input, slug);

        await repo.AddDefinitionAsync(def, ct); // SaveChangesAsync first
        await auditLog.LogAsync(
            CampsAuditActions.CampRoleDefinitionCreated,
            nameof(CampRoleDefinition),
            def.Id,
            $"Created camp role definition '{def.Name}'.",
            actorUserId);

        return new(true);
    }

    private static void ApplyDefinitionFields(CampRoleDefinition definition, CampRoleDefinitionInput input, string slug)
    {
        definition.Name = input.Name;
        definition.Slug = slug;
        definition.Description = input.Description;
        definition.SlotCount = input.SlotCount;
        definition.MinimumRequired = input.MinimumRequired;
        definition.SortOrder = input.SortOrder;
    }

    private static string? ValidateDefinitionInput(int slotCount, int minimumRequired, string slug)
    {
        if (minimumRequired < 0 || minimumRequired > slotCount)
            return $"MinimumRequired must satisfy 0 ≤ MinimumRequired ≤ SlotCount (got SlotCount={slotCount}, MinimumRequired={minimumRequired}).";
        if (string.IsNullOrWhiteSpace(slug))
            return "Slug is required.";
        var normalized = slug.Trim().ToLowerInvariant();
        if (!SlugHelper.IsValidKebabSlug(normalized))
            return "Slug must be kebab-case (lowercase letters, digits, and hyphens; no leading, trailing, or consecutive hyphens; max 60 chars).";
        // "create" would route to CampAdminController.CreateRole instead of RolesDrillDown.
        return string.Equals(normalized, "create", StringComparison.Ordinal)
            ? "\"create\" is reserved and cannot be used as a camp role slug." : null;
    }

    public async Task<UpdateCampRoleDefinitionResult> UpdateDefinitionAsync(Guid id, CampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
    {
        if (ValidateDefinitionInput(input.SlotCount, input.MinimumRequired, input.Slug) is { } error)
            return UpdateCampRoleDefinitionResult.Rejected(error);
        var slug = input.Slug.Trim().ToLowerInvariant();

        var existing = await repo.GetDefinitionByIdAsync(id, ct);
        if (existing is null) return UpdateCampRoleDefinitionResult.NotFound;

        // Special role definitions (Camp Lead, Workshop Lead) are immutable except
        // for SlotCount + Description.
        if (existing.SpecialRole != CampSpecialRole.None)
        {
            if (!string.Equals(existing.Name, input.Name, StringComparison.Ordinal)
                || !string.Equals(existing.Slug, slug, StringComparison.Ordinal)
                || existing.SortOrder != input.SortOrder
                || existing.MinimumRequired != input.MinimumRequired)
            {
                return UpdateCampRoleDefinitionResult.Rejected(
                    $"Camp role '{existing.Name}' is a system role; only SlotCount and Description can be changed.");
            }
        }

        if (await repo.DefinitionNameExistsAsync(input.Name, excludingId: id, ct))
            return UpdateCampRoleDefinitionResult.Rejected($"A camp role definition named '{input.Name}' already exists.");

        if (await repo.DefinitionSlugExistsAsync(slug, excludingId: id, ct))
            return UpdateCampRoleDefinitionResult.Rejected($"A camp role definition with slug '{slug}' already exists.");

        var now = clock.GetCurrentInstant();
        var updated = await repo.UpdateDefinitionAsync(id, def =>
        {
            ApplyDefinitionFields(def, input, slug);
            def.UpdatedAt = now;
        }, ct);

        if (!updated) return UpdateCampRoleDefinitionResult.NotFound;

        await auditLog.LogAsync(
            CampsAuditActions.CampRoleDefinitionUpdated,
            nameof(CampRoleDefinition),
            id,
            $"Updated camp role definition '{input.Name}'.",
            actorUserId);

        return UpdateCampRoleDefinitionResult.Updated(input.Name);
    }

    public async Task<bool> DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var existing = await repo.GetDefinitionByIdAsync(id, ct);
        if (existing is null) return false;
        if (existing.SpecialRole != CampSpecialRole.None)
        {
            throw new InvalidOperationException(
                $"Camp role '{existing.Name}' is a system role and cannot be deactivated.");
        }

        var now = clock.GetCurrentInstant();
        var updated = await repo.UpdateDefinitionAsync(id, def =>
        {
            if (def.DeactivatedAt is null)
                def.DeactivatedAt = now;
            def.UpdatedAt = now;
        }, ct);
        if (!updated) return false;

        await auditLog.LogAsync(
            CampsAuditActions.CampRoleDefinitionDeactivated,
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
            CampsAuditActions.CampRoleDefinitionReactivated,
            nameof(CampRoleDefinition), id,
            "Reactivated camp role definition.", actorUserId);
        return true;
    }

    public Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default) =>
        roleReader.BuildPanelAsync(campSeasonId, ct);

    public async Task<AssignCampRoleOutcome> AssignAsync(
        Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct = default,
        Guid? addUserId = null)
    {
        var def = await repo.GetDefinitionByIdAsync(roleDefinitionId, ct);
        if (def is null) return AssignCampRoleOutcome.RoleNotFound;
        if (def.DeactivatedAt is not null) return AssignCampRoleOutcome.RoleDeactivated;

        CampMemberLookup? memberLookup;
        if (addUserId is { } requestedUserId)
        {
            var user = await userService.GetUserInfoAsync(requestedUserId, ct);
            if (user is null || user.IsTombstone) return AssignCampRoleOutcome.MemberNotFound;
            addUserId = user.Id;
            var member = (await repo.GetAllMembershipsForUserAsync(user.Id, ct))
                .FirstOrDefault(m => m.CampSeasonId == campSeasonId && m.Status != CampMemberStatus.Removed);
            campMemberId = member?.Id ?? Guid.Empty;
            memberLookup = new CampMemberLookup(campSeasonId, user.Id, CampMemberStatus.Active);
        }
        else
        {
            memberLookup = await campAccess.GetCampMemberStatusAsync(campMemberId, ct);
            if (memberLookup is null) return AssignCampRoleOutcome.MemberNotFound;
            if (memberLookup.CampSeasonId != campSeasonId) return AssignCampRoleOutcome.MemberSeasonMismatch;
            if (memberLookup.Status != CampMemberStatus.Active) return AssignCampRoleOutcome.MemberNotActive;
        }

        if (campMemberId != Guid.Empty && await repo.AssignmentExistsAsync(campSeasonId, roleDefinitionId, campMemberId, ct))
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

        var (inserted, membership) = await repo.AddAssignmentAsync(assignment, ct, addUserId);
        if (!inserted)
        {
            // Repo translated the unique-index race.
            return AssignCampRoleOutcome.AlreadyHoldsRole;
        }

        campMemberId = membership?.MemberId ?? campMemberId;
        if (membership is { Outcome: CampMemberInsertOutcome.Created })
        {
            await auditLog.LogAsync(
                CampsAuditActions.CampMemberAddedByLead, nameof(CampMember), campMemberId,
                "Lead added human as active camp member.", actorUserId,
                relatedEntityId: memberLookup.UserId, relatedEntityType: AuditSubjectTypes.User);
        }

        await auditLog.LogAsync(
            CampsAuditActions.CampRoleAssigned,
            nameof(CampRoleAssignment),
            assignment.Id,
            $"Assigned role '{def.Name}' to member.",
            actorUserId,
            relatedEntityId: campMemberId, relatedEntityType: nameof(CampMember));

        // Assignment is committed; request cancellation must not skip its notification.
        try
        {
            var recipientId = memberLookup.UserId;
            var culture = CultureInfo.GetCultureInfo("en");
            try
            {
                var person = await userService.GetUserInfoAsync(memberLookup.UserId, CancellationToken.None);
                recipientId = person?.Id ?? memberLookup.UserId;
                var language = person?.PreferredLanguage;
                culture = CultureInfo.GetCultureInfo(language.IsSupportedCultureCode() ? language! : "en");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve role notification language for user {UserId}; using English", memberLookup.UserId);
            }

            await notificationEmitter.SendAsync(
                source: CampsNotificationSources.CampRoleAssigned,
                notificationClass: NotificationClass.Informational,
                priority: NotificationPriority.Normal,
                title: string.Format(culture, NoticeResources.GetString("Camps_Notification_RoleAssigned", culture)!, def.Name),
                recipientUserIds: [recipientId],
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notification failed for CampRoleAssigned (assignment {AssignmentId}).", assignment.Id);
        }

        return AssignCampRoleOutcome.Assigned;
    }

    public Task<CampRoleAssignmentInfo?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default) =>
        roleReader.GetAssignmentByIdAsync(assignmentId, ct);

    public async Task<bool> UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct = default)
    {
        var assignment = await repo.GetAssignmentByIdAsync(assignmentId, ct);
        if (assignment is null) return false;

        var deleted = await repo.DeleteAssignmentAsync(assignmentId, ct);
        if (!deleted) return false;

        await auditLog.LogAsync(
            CampsAuditActions.CampRoleUnassigned,
            nameof(CampRoleAssignment),
            assignmentId,
            "Unassigned camp role.",
            actorUserId,
            relatedEntityId: assignment.CampMemberId,
            relatedEntityType: nameof(CampMember));

        return true;
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CampDirectoryRoleSummary>>>
        GetDirectoryRoleSummariesAsync(Guid eventSettingsId, CancellationToken ct = default) =>
        roleReader.GetDirectoryRoleSummariesAsync(eventSettingsId, ct);

    public Task<CampComplianceMatrixData?> BuildComplianceMatrixAsync(Guid eventSettingsId, CancellationToken ct = default) =>
        roleReader.BuildComplianceMatrixAsync(eventSettingsId, ct);

    public Task<IReadOnlyList<CampSpecialRole>> GetMissingSpecialRolesAsync(CancellationToken ct = default) =>
        roleReader.GetMissingSpecialRolesAsync(ct);

    public async Task<int> SeedSystemRolesAsync(
        Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        // Idempotent seed across every non-None CampSpecialRole value. The
        // enum is the source of truth — adding a new value automatically picks
        // it up on the next seed-button click.
        var definitionsCreated = 0;
        foreach (var specialRole in Enum.GetValues<CampSpecialRole>())
        {
            if (specialRole == CampSpecialRole.None) continue;
            var (name, slug, sortOrder, slotCount, minimumRequired, description) = specialRole switch
            {
                CampSpecialRole.Lead => (
                    CampSystemRoles.CampLeadName,
                    CampSystemRoles.CampLeadSlug,
                    CampSystemRoles.CampLeadSortOrder,
                    CampSystemRoles.CampLeadSlotCount,
                    CampSystemRoles.CampLeadMinimumRequired,
                    "Authorizes camp-management actions (Edit, members, roles, leads) and camp-event submission. Sort-to-top of the camp's Roles panel."),
                CampSpecialRole.Workshop => (
                    CampSystemRoles.WorkshopLeadName,
                    CampSystemRoles.WorkshopLeadSlug,
                    CampSystemRoles.WorkshopLeadSortOrder,
                    CampSystemRoles.WorkshopLeadSlotCount,
                    CampSystemRoles.WorkshopLeadMinimumRequired,
                    "Authorizes camp-event submission alongside Camp Lead. Does not grant general camp-management authority."),
                _ => throw new InvalidOperationException($"No seed defaults for special role '{specialRole}'."),
            };

            if (await repo.GetSpecialDefinitionAsync(specialRole, ct) is null)
            {
                var definition = new CampRoleDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    Slug = slug,
                    Description = description,
                    SlotCount = slotCount,
                    MinimumRequired = minimumRequired,
                    SortOrder = sortOrder,
                    SpecialRole = specialRole,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await repo.AddDefinitionAsync(definition, ct);
                await auditLog.LogAsync(
                    CampsAuditActions.CampRoleDefinitionCreated,
                    nameof(CampRoleDefinition),
                    definition.Id,
                    $"Seeded system camp role '{name}'.",
                    actorUserId);
                definitionsCreated++;
            }
        }

        return definitionsCreated;
    }

    public Task<CampRoleDrillDownData?> BuildDrillDownAsync(Guid roleDefinitionId, Guid eventSettingsId, CancellationToken ct = default) =>
        roleReader.BuildDrillDownAsync(roleDefinitionId, eventSettingsId, ct);

    /// <inheritdoc />
    public Task<Dictionary<string, Guid[]>> GetExpectedAsync(string? groupKey = null, CancellationToken ct = default) =>
        roleReader.GetExpectedAsync(groupKey, ct);

    public string BuildGroupKey(int year, string slug) => roleReader.BuildGroupKey(year, slug);

    // Leaf carve: the camp-role verbs the dev seeders drive (see ICampRoleSeeding).

    /// <inheritdoc />
    async Task<IReadOnlyList<CampRoleDefinitionSeedInfo>> ICampRoleSeeding.ListDefinitionsForSeedAsync(
        bool includeDeactivated, CancellationToken cancellationToken)
    {
        var definitions = await ListDefinitionsAsync(includeDeactivated, cancellationToken);
        return definitions.Select(d => new CampRoleDefinitionSeedInfo(d.Id, d.Slug, d.Name)).ToList();
    }

    /// <inheritdoc />
    async Task ICampRoleSeeding.CreateDefinitionForSeedAsync(
        string name, string slug, string? description, int slotCount, int minimumRequired,
        int sortOrder, Guid actorUserId, CancellationToken cancellationToken)
    {
        var result = await CreateDefinitionAsync(
            new CampRoleDefinitionInput(name, slug, description, slotCount, minimumRequired, sortOrder),
            actorUserId, cancellationToken);
        if (!result.Succeeded) throw new InvalidOperationException(result.ErrorMessage);
    }

    /// <inheritdoc />
    Task<int> ICampRoleSeeding.SeedSystemRolesAsync(Guid actorUserId, CancellationToken cancellationToken) =>
        SeedSystemRolesAsync(actorUserId, cancellationToken);

    /// <inheritdoc />
    async Task<Guid?> ICampRoleSeeding.GetDefinitionIdBySlugAsync(string slug, CancellationToken cancellationToken) =>
        (await GetDefinitionBySlugAsync(slug, cancellationToken))?.Id;

}
