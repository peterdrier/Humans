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

    public Task<bool> DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<AssignCampRoleOutcome> AssignAsync(Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<int> RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<CampRoleComplianceReport> GetComplianceReportAsync(int year, CancellationToken ct = default)
        => throw new NotSupportedException();
}
