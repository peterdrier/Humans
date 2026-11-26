using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Auth.Contracts;

public interface ISystemRoleService : IApplicationService
{
    Task<(IReadOnlyList<SystemRoleSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default);

    Task<IReadOnlyList<SystemRoleSummarySnapshot>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

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
    Task<IReadOnlyList<SystemRoleSnapshot>> GetActiveForUserAsync(Guid userId, CancellationToken ct = default);
}

public sealed record SystemRoleSnapshot(
    string RoleName,
    Instant? ValidTo);

public sealed record SystemRoleSummarySnapshot(
    Guid Id,
    Guid UserId,
    string? UserEmail,
    string UserDisplayName,
    string RoleName,
    Instant ValidFrom,
    Instant? ValidTo,
    string? Notes,
    Guid CreatedByUserId,
    string? CreatedByDisplayName)
{
    public bool IsActive(Instant now) =>
        ValidFrom <= now && (ValidTo is null || ValidTo > now);
}
