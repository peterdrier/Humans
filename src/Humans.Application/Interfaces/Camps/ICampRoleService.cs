using Humans.Application.Interfaces;
using Humans.Application.Services.Camps;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Camps;

public interface ICampRoleService : IApplicationService
{
    // Definitions

    Task<IReadOnlyList<CampRoleDefinitionInfo>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default);

    Task<CampRoleDefinitionInfo?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default);

    Task<CampRoleDefinition> CreateDefinitionAsync(CreateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default);

    Task<bool> UpdateDefinitionAsync(Guid id, UpdateCampRoleDefinitionInput input, Guid actorUserId, CancellationToken ct = default);

    Task<bool> DeactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    Task<bool> ReactivateDefinitionAsync(Guid id, Guid actorUserId, CancellationToken ct = default);

    // Per-camp assignments

    Task<CampRolesPanelData> BuildPanelAsync(Guid campSeasonId, CancellationToken ct = default);

    Task<AssignCampRoleOutcome> AssignAsync(Guid campSeasonId, Guid roleDefinitionId, Guid campMemberId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Loads a single assignment (including its season) so callers can verify
    /// camp ownership before mutating it. Used by the per-camp UnassignRole
    /// controller action for the C2 cross-camp ownership check.
    /// </summary>
    Task<CampRoleAssignmentInfo?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<bool> UnassignAsync(Guid assignmentId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Cascade hook — deletes every role assignment for the given camp member.
    /// Called by <see cref="ICampService.LeaveCampAsync"/> and
    /// <see cref="ICampService.WithdrawCampMembershipRequestAsync"/>.
    /// </summary>
    Task<int> RemoveAllForMemberAsync(Guid campMemberId, Guid actorUserId, CancellationToken ct = default);

    // Reporting

    Task<CampRoleComplianceReport> GetComplianceReportAsync(int year, CancellationToken ct = default);
}

public sealed record CreateCampRoleDefinitionInput(
    string Name,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder);

public sealed record UpdateCampRoleDefinitionInput(
    string Name,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder);

public sealed record CampRoleDefinitionInfo(
    Guid Id,
    string Name,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder,
    Instant CreatedAt,
    Instant UpdatedAt,
    Instant? DeactivatedAt)
{
    public bool IsActive => DeactivatedAt is null;
}

public sealed record CampRoleAssignmentInfo(
    Guid Id,
    Guid CampSeasonId,
    Guid CampRoleDefinitionId,
    Guid CampMemberId);
