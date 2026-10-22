using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Auth.Contracts;

public interface IRoleAssignmentService : IApplicationService
{
    Task<(IReadOnlyList<RoleAssignmentSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default);

    Task<RoleAssignmentDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignmentSummarySnapshot>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> HasActiveRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default);

    /// <summary>Any role at all: tells governance members from plain volunteers.</summary>
    Task<bool> HasAnyActiveAssignmentAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName, CancellationToken ct = default);

    /// <summary>Active means ValidFrom &lt;= now &lt; ValidTo, with a null ValidTo open-ended. Served from the row cache.</summary>
    Task<IReadOnlyList<RoleAssignmentSnapshot>> GetActiveForUserAsync(Guid userId, CancellationToken ct = default);
}

public sealed record RoleAssignmentSnapshot(
    string RoleName,
    Instant? ValidTo);

public sealed record RoleAssignmentDetailSnapshot(
    Guid UserId,
    string RoleName,
    string UserDisplayName);

public sealed record RoleAssignmentSummarySnapshot(
    Guid Id,
    Guid UserId,
    string? UserEmail,
    string UserDisplayName,
    string RoleName,
    Instant ValidFrom,
    Instant? ValidTo,
    string? Notes,
    Guid CreatedByUserId,
    string? CreatedByDisplayName,
    Instant CreatedAt)
{
    public bool IsActive(Instant now) =>
        ValidFrom <= now && (ValidTo is null || ValidTo > now);
}
