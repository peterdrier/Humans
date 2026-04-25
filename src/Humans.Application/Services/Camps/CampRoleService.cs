using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
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

    public Task<CampRoleDefinition> CreateDefinitionAsync(CreateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<bool> UpdateDefinitionAsync(Guid id, UpdateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default)
        => throw new NotSupportedException();

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
