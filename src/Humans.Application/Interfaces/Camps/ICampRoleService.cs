using Humans.Application.Services.Camps;
using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Camps;

public interface ICampRoleService
{
    // Definitions

    Task<IReadOnlyList<CampRoleDefinition>> ListDefinitionsAsync(bool includeDeactivated, CancellationToken ct = default);

    Task<CampRoleDefinition?> GetDefinitionByIdAsync(Guid id, CancellationToken ct = default);

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
    Task<CampRoleAssignment?> GetAssignmentByIdAsync(Guid assignmentId, CancellationToken ct = default);

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
    int SortOrder,
    bool IsRequired);

public sealed record UpdateCampRoleDefinitionInput(
    string Name,
    string? Description,
    int SlotCount,
    int MinimumRequired,
    int SortOrder,
    bool IsRequired);
