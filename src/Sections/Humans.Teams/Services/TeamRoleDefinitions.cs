using Humans.GoogleIntegration.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed class TeamRoleDefinitions(
    ITeamRepository repo,
    IAuditLogService auditLogService,
    IClock clock,
    IServiceProvider serviceProvider,
    TeamSnapshotReader snapshotReader,
    TeamMembershipOutbox membershipOutbox) : IApplicationService
{
    private ISystemTeamSync SystemTeamSync => serviceProvider.GetRequiredService<ISystemTeamSync>();

    internal async Task<TeamRoleDefinitionMutationResult> CreateRoleDefinitionAsync(
        Guid teamId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var team = await repo.GetByIdAsync(teamId, cancellationToken);
        if (team is null)
            return new(Refusal: $"Team {teamId} not found");

        if (RoleCapacityRefusal(slotCount, priorities, estimatedHours) is { } prioritiesRefusal)
            return new(Refusal: prioritiesRefusal);
        if (RoleNameRefusal(name) is { } nameRefusal)
            return new(Refusal: nameRefusal);

        var lowerName = name.ToLowerInvariant();
        var nameExists = await repo.RoleDefinitionNameExistsAsync(teamId, lowerName, excludingId: null, cancellationToken);
        if (nameExists)
            return new(Refusal: $"A role definition with name '{name}' already exists for this team");

        var canManage = await snapshotReader.CanUserApproveRequestsForTeamAsync(teamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        var now = clock.GetCurrentInstant();
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = name,
            Description = description,
            SlotCount = slotCount,
            EstimatedHours = estimatedHours,
            Priorities = priorities,
            SortOrder = sortOrder,
            IsPublic = isPublic,
            Period = period,
            CreatedAt = now,
            UpdatedAt = now
        };

        await repo.AddRoleDefinitionAsync(definition, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionCreated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' created for team {team.Name}",
            actorUserId,
            relatedEntityId: teamId, relatedEntityType: nameof(Team));

        return new(Definition: definition);
    }

    internal async Task<TeamRoleDefinitionMutationResult> UpdateRoleDefinitionAsync(
        Guid roleDefinitionId, string name, string? description, int slotCount,
        List<SlotPriority> priorities, int sortOrder, bool isManagement, RolePeriod period, Guid actorUserId,
        bool isPublic = true,
        bool canToggleManagement = true,
        int? estimatedHours = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return new(Refusal: $"Role definition {roleDefinitionId} not found");
        var team = await snapshotReader.GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await snapshotReader.CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        if (RoleCapacityRefusal(slotCount, priorities, estimatedHours) is { } prioritiesRefusal)
            return new(Refusal: prioritiesRefusal);

        if (!canToggleManagement)
            isManagement = definition.IsManagement;

        if (slotCount < definition.Assignments.Count
            || definition.Assignments.Any(assignment => assignment.SlotIndex >= slotCount))
            return new(Refusal: $"Cannot reduce slot count to {slotCount} — unassign humans from the slots being removed first");

        if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
        {
            if (RoleNameRefusal(name) is { } nameRefusal)
                return new(Refusal: nameRefusal);
            var lowerName = name.ToLowerInvariant();
            var nameExists = await repo.RoleDefinitionNameExistsAsync(definition.TeamId, lowerName, excludingId: roleDefinitionId, cancellationToken);
            if (nameExists)
                return new(Refusal: $"A role definition with name '{name}' already exists for this team");
        }

        if (isManagement && !definition.IsManagement)
        {
            var existingManagement = await repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                return new(Refusal: "Another role in this team is already marked as the management role");
        }

        definition.Name = name;
        definition.Description = description;
        definition.SlotCount = slotCount;
        definition.EstimatedHours = estimatedHours;
        definition.Priorities = priorities;
        definition.SortOrder = sortOrder;

        var managementChanged = definition.IsManagement != isManagement;

        definition.IsPublic = isPublic;
        definition.IsManagement = isManagement;
        definition.Period = period;
        definition.UpdatedAt = clock.GetCurrentInstant();

        await repo.PersistRoleDefinitionUpdateAsync(
            definition, managementChanged, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{name}' updated for team {team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        if (managementChanged)
            await SyncRoleAssigneesAsync(definition, cancellationToken);

        return new(Definition: definition);
    }

    internal async Task<TeamRoleDefinitionMutationResult> DeleteRoleDefinitionAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return new(Refusal: $"Role definition {roleDefinitionId} not found");
        var team = await snapshotReader.GetRoleDefinitionTeamAsync(definition, cancellationToken);

        if (definition.IsManagement && definition.Assignments.Count > 0)
            return new(Refusal: "Cannot delete the management role while members are assigned to it. Unassign all members first.");

        var canManage = await snapshotReader.CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        await repo.RemoveRoleDefinitionAsync(definition, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionDeleted, nameof(TeamRoleDefinition), definition.Id,
            $"Role definition '{definition.Name}' deleted from team {team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        return new();
    }

    internal async Task<TeamRoleDefinitionMutationResult> ToggleRoleIsManagementAsync(
        Guid roleDefinitionId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return new(Refusal: $"Role definition {roleDefinitionId} not found");
        var team = await snapshotReader.GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await snapshotReader.CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return new(Refusal: "User does not have permission to manage role definitions for this team");

        var isManagement = !definition.IsManagement;
        if (isManagement)
        {
            if (definition.Assignments.Count > 0)
                return new(Refusal: "Cannot set IsManagement while members are assigned to the role");

            var existingManagement = await repo.OtherRoleHasIsManagementAsync(
                definition.TeamId, roleDefinitionId, cancellationToken);
            if (existingManagement)
                return new(Refusal: "Another role in this team is already marked as the management role");
        }

        definition.IsManagement = isManagement;
        definition.UpdatedAt = clock.GetCurrentInstant();

        await repo.PersistRoleDefinitionUpdateAsync(
            definition, managementChanged: true, cancellationToken);

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleDefinitionUpdated, nameof(TeamRoleDefinition), definition.Id,
            $"IsManagement set to {isManagement} on role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: definition.TeamId, relatedEntityType: nameof(Team));

        await SyncRoleAssigneesAsync(definition, cancellationToken);

        return new(Definition: definition);
    }

    private async Task SyncRoleAssigneesAsync(TeamRoleDefinition definition, CancellationToken cancellationToken)
    {
        foreach (var userId in definition.Assignments.Select(a => a.TeamMember.UserId).Distinct())
            await SystemTeamSync.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, cancellationToken);
    }

    private static string? RoleNameRefusal(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Role name cannot be empty";

        if (name.Length > 100)
            return "Role name cannot exceed 100 characters";
        return null;
    }

    private static string? RoleCapacityRefusal(int slotCount, List<SlotPriority> priorities, int? estimatedHours)
    {
        if (slotCount < 1)
            return "Slot count must be at least 1";

        if (priorities.Count != slotCount)
            return $"Priorities count ({priorities.Count}) must match slot count ({slotCount})";

        if (priorities.Any(priority => !Enum.IsDefined(priority)))
            return "Each slot priority must be a defined priority value";
        if (estimatedHours is < 0)
            return "Estimated hours cannot be negative";
        return null;
    }

    private async Task<(TeamRoleDefinition? Definition, Team? Team, string? Refusal)> ResolveRoleAssignmentManagementAsync(
        Guid roleDefinitionId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var definition = await repo.FindRoleDefinitionForMutationAsync(roleDefinitionId, cancellationToken);
        if (definition is null)
            return (null, null, $"Role definition {roleDefinitionId} not found");
        var team = await snapshotReader.GetRoleDefinitionTeamAsync(definition, cancellationToken);

        var canManage = await snapshotReader.CanUserApproveRequestsForTeamAsync(definition.TeamId, actorUserId, cancellationToken);
        if (!canManage)
            return (null, null, "User does not have permission to manage role assignments for this team");

        return (definition, team, null);
    }

    internal async Task<TeamRoleAssignmentMutationResult> AssignToRoleAsync(
        Guid roleDefinitionId, Guid targetUserId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var (definition, team, refusal) = await ResolveRoleAssignmentManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken);
        if (definition is null || team is null)
            return new(Refusal: refusal);

        var resolvedUserId = await snapshotReader.ResolveMembershipTargetAsync(targetUserId, cancellationToken);
        if (resolvedUserId is null)
            return new(Refusal: TeamSnapshotReader.UnavailableMembershipTargetMessage);
        targetUserId = resolvedUserId.Value;
        var existingMember = await repo.FindActiveMemberForMutationAsync(definition.TeamId, targetUserId, cancellationToken);
        var now = clock.GetCurrentInstant();

        TeamMember? autoAddMember = null;
        GoogleSyncOutboxEvent? outboxEvent = null;
        if (existingMember is null)
        {
            if (team.IsSystemTeam)
                return new(Refusal: "Cannot add members to system teams via role assignment; only existing members can be assigned to roles on system teams.");

            autoAddMember = new TeamMember
            {
                Id = Guid.NewGuid(),
                TeamId = definition.TeamId,
                UserId = targetUserId,
                Role = TeamMemberRole.Member,
                JoinedAt = now
            };
            outboxEvent = await membershipOutbox.BuildOutboxEventAsync(
                autoAddMember.Id, definition.TeamId, targetUserId,
                GoogleSyncOutboxEventTypes.AddUserToTeamResources, cancellationToken);
        }

        var result = await membershipOutbox.AssignToRoleWithOutboxAsync(
            roleDefinitionId,
            targetUserId,
            actorUserId,
            autoAddMember,
            outboxEvent,
            promoteToCoordinator: definition.IsManagement,
            now,
            cancellationToken);

        if (!result.Succeeded)
            return result;

        if (autoAddMember is not null)
        {
            await auditLogService.LogAsync(
                TeamsAuditActions.TeamMemberAdded, nameof(Team), definition.TeamId,
                $"Auto-added to {team.Name} via role assignment",
                actorUserId,
                relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);
        }

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleAssigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Assigned to role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);

        // Management roles promote: reconcile Coordinators in the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);

        return result;
    }

    internal async Task<TeamRoleAssignmentMutationResult> UnassignFromRoleAsync(
        Guid roleDefinitionId, Guid teamMemberId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var (definition, team, refusal) = await ResolveRoleAssignmentManagementAsync(
            roleDefinitionId, actorUserId, cancellationToken);
        if (definition is null || team is null)
            return new(Refusal: refusal);

        var result = await repo.UnassignFromRoleAsync(
            roleDefinitionId, teamMemberId, cancellationToken);
        if (!result.Succeeded)
            return result;
        var targetUserId = result.Assignment!.TeamMember.UserId;

        await auditLogService.LogAsync(
            TeamsAuditActions.TeamRoleUnassigned, nameof(TeamRoleDefinition), roleDefinitionId,
            $"Unassigned from role '{definition.Name}' in {team.Name}",
            actorUserId,
            relatedEntityId: targetUserId, relatedEntityType: AuditSubjectTypes.User);

        // Losing a management role can demote: reconcile Coordinators in the mutation.
        await SystemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.Coordinators, cancellationToken);
        return result;
    }

}
