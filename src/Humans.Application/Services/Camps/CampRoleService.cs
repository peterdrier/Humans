using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Camps;

public sealed class CampRoleService : ICampRoleService
{
    private readonly ICampRoleRepository _repo;
    private readonly ICampService _campService;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLog;
    private readonly INotificationEmitter _notificationEmitter;
    private readonly IClock _clock;
    private readonly ILogger<CampRoleService> _logger;

    public CampRoleService(
        ICampRoleRepository repo,
        ICampService campService,
        IUserService userService,
        IAuditLogService auditLog,
        INotificationEmitter notificationEmitter,
        IClock clock,
        ILogger<CampRoleService> logger)
    {
        _repo = repo;
        _campService = campService;
        _userService = userService;
        _auditLog = auditLog;
        _notificationEmitter = notificationEmitter;
        _clock = clock;
        _logger = logger;
    }

    public Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default)
        => _repo.ListDefinitionsAsync(includeDeactivated, ct);

    public Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default)
        => _repo.GetDefinitionByIdAsync(id, ct);

    public async Task<CampRoleDefinition> CreateDefinitionAsync(CreateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateMinimumRequired(input.SlotCount, input.MinimumRequired);

        if (await _repo.DefinitionNameExistsAsync(input.Name, excludingId: null, ct))
            throw new InvalidOperationException($"A camp role definition named '{input.Name}' already exists.");

        var now = _clock.GetCurrentInstant();
        var def = new CampRoleDefinition
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Description = input.Description,
            SlotCount = input.SlotCount,
            MinimumRequired = input.MinimumRequired,
            SortOrder = input.SortOrder,
            IsRequired = input.IsRequired,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _repo.AddDefinitionAsync(def, ct); // SaveChangesAsync first
        await _auditLog.LogAsync(
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

    public async Task<bool> UpdateDefinitionAsync(Guid id, UpdateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateMinimumRequired(input.SlotCount, input.MinimumRequired);

        if (await _repo.DefinitionNameExistsAsync(input.Name, excludingId: id, ct))
            throw new InvalidOperationException($"A camp role definition named '{input.Name}' already exists.");

        var now = _clock.GetCurrentInstant();
        var updated = await _repo.UpdateDefinitionAsync(id, def =>
        {
            def.Name = input.Name;
            def.Description = input.Description;
            def.SlotCount = input.SlotCount;
            def.MinimumRequired = input.MinimumRequired;
            def.SortOrder = input.SortOrder;
            def.IsRequired = input.IsRequired;
            def.UpdatedAt = now;
        }, ct);

        if (!updated) return false;

        await _auditLog.LogAsync(
            AuditAction.CampRoleDefinitionUpdated,
            nameof(CampRoleDefinition),
            id,
            $"Updated camp role definition '{input.Name}'.",
            actorUserId);

        return true;
    }

    public async Task<bool> DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var updated = await _repo.UpdateDefinitionAsync(id, def =>
        {
            if (def.DeactivatedAt is null)
                def.DeactivatedAt = now;
            def.UpdatedAt = now;
        }, ct);
        if (!updated) return false;

        await _auditLog.LogAsync(
            AuditAction.CampRoleDefinitionDeactivated,
            nameof(CampRoleDefinition), id,
            "Deactivated camp role definition.", actorUserId);
        return true;
    }

    public async Task<bool> ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
    {
        var now = _clock.GetCurrentInstant();
        var updated = await _repo.UpdateDefinitionAsync(id, def =>
        {
            def.DeactivatedAt = null;
            def.UpdatedAt = now;
        }, ct);
        if (!updated) return false;

        await _auditLog.LogAsync(
            AuditAction.CampRoleDefinitionReactivated,
            nameof(CampRoleDefinition), id,
            "Reactivated camp role definition.", actorUserId);
        return true;
    }

    public Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public async Task<AssignCampRoleOutcome> AssignAsync(
        Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct = default)
    {
        var def = await _repo.GetDefinitionByIdAsync(roleDefinitionId, ct);
        if (def is null) return AssignCampRoleOutcome.RoleNotFound;
        if (def.DeactivatedAt is not null) return AssignCampRoleOutcome.RoleDeactivated;

        var memberLookup = await _campService.GetCampMemberStatusAsync(campMemberId, ct);
        if (memberLookup is null) return AssignCampRoleOutcome.MemberNotFound;
        if (memberLookup.CampSeasonId != campSeasonId) return AssignCampRoleOutcome.MemberSeasonMismatch;
        if (memberLookup.Status != CampMemberStatus.Active) return AssignCampRoleOutcome.MemberNotActive;

        if (await _repo.AssignmentExistsAsync(campSeasonId, roleDefinitionId, campMemberId, ct))
            return AssignCampRoleOutcome.AlreadyHoldsRole;

        var existingCount = await _repo.CountAssignmentsForSeasonAndDefinitionAsync(campSeasonId, roleDefinitionId, ct);
        if (existingCount >= def.SlotCount)
            return AssignCampRoleOutcome.SlotCapReached;

        var now = _clock.GetCurrentInstant();
        var assignment = new CampRoleAssignment
        {
            Id = Guid.NewGuid(),
            CampSeasonId = campSeasonId,
            CampRoleDefinitionId = roleDefinitionId,
            CampMemberId = campMemberId,
            AssignedAt = now,
            AssignedByUserId = actorUserId,
        };

        var inserted = await _repo.AddAssignmentAsync(assignment, ct);
        if (!inserted)
        {
            // I5 fix — repo translated the unique-index race
            return AssignCampRoleOutcome.AlreadyHoldsRole;
        }

        await _auditLog.LogAsync(
            AuditAction.CampRoleAssigned,
            nameof(CampRoleAssignment),
            assignment.Id,
            $"Assigned role '{def.Name}' to member.",
            actorUserId,
            relatedEntityId: campMemberId, relatedEntityType: nameof(CampMember));

        try
        {
            await _notificationEmitter.SendAsync(
                source: NotificationSource.CampRoleAssigned,
                notificationClass: NotificationClass.Informational,
                priority: NotificationPriority.Normal,
                title: $"You were assigned the {def.Name} role.",
                recipientUserIds: new[] { memberLookup.UserId },
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification failed for CampRoleAssigned (assignment {AssignmentId}).", assignment.Id);
        }

        return AssignCampRoleOutcome.Assigned;
    }

    public async Task<bool> UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct = default)
    {
        var assignment = await _repo.GetAssignmentByIdAsync(assignmentId, ct);
        if (assignment is null) return false;

        var deleted = await _repo.DeleteAssignmentAsync(assignmentId, ct);
        if (!deleted) return false;

        await _auditLog.LogAsync(
            AuditAction.CampRoleUnassigned,
            nameof(CampRoleAssignment),
            assignmentId,
            "Unassigned camp role.",
            actorUserId,
            relatedEntityId: assignment.CampMemberId,
            relatedEntityType: nameof(CampMember));

        return true;
    }

    public Task<int> RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<CampRoleComplianceReport> GetComplianceReportAsync(int year, CancellationToken ct = default)
        => throw new NotSupportedException();
}
