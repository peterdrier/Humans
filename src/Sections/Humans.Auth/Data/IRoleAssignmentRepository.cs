using Humans.Base.Interfaces.Repositories;
using Humans.Auth.Domain;
using NodaTime;

namespace Humans.Auth.Data;

internal interface IRoleAssignmentRepository : IRepository
{
    /// <summary>Tracked, for <see cref="UpdateAsync"/>. Returns null if it doesn't exist.</summary>
    Task<RoleAssignment?> FindForMutationAsync(Guid assignmentId, CancellationToken ct = default);

    Task<RoleAssignment?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default);

    /// <summary>Newest <c>ValidFrom</c> first.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="roleNames"/> is every stored name of one role (current and former), or null for all roles.
    /// </summary>
    Task<(IReadOnlyList<RoleAssignment> Items, int TotalCount)> GetFilteredAsync(
        IReadOnlyList<string>? roleNames,
        bool activeOnly,
        int page,
        int pageSize,
        Instant now,
        CancellationToken ct = default);

    Task<bool> HasOverlappingAssignmentAsync(
        Guid userId,
        IReadOnlyList<string> roleNames,
        Instant validFrom,
        Instant? validTo,
        CancellationToken ct = default);

    Task<bool> HasActiveRoleAsync(
        Guid userId,
        IReadOnlyList<string> roleNames,
        Instant now,
        CancellationToken ct = default);

    Task<bool> HasAnyActiveAssignmentAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(
        Instant now,
        CancellationToken ct = default);

    /// <summary>Tracked, for <see cref="UpdateManyAsync"/>.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetActiveForUserForMutationAsync(
        Guid userId,
        Instant now,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(
        IReadOnlyList<string> roleNames,
        Instant now,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(
        Instant now,
        CancellationToken ct = default);

    /// <summary>Tracked, for <see cref="UpdateManyAsync"/>: every row, active or ended, stored under one of the names.</summary>
    Task<IReadOnlyList<RoleAssignment>> GetByRoleNamesForMutationAsync(
        IReadOnlyList<string> roleNames,
        CancellationToken ct = default);

    Task AddAsync(RoleAssignment assignment, CancellationToken ct = default);

    Task UpdateAsync(RoleAssignment assignment, CancellationToken ct = default);

    /// <summary>One transaction. No-op for an empty list.</summary>
    Task UpdateManyAsync(IReadOnlyList<RoleAssignment> assignments, CancellationToken ct = default);

    /// <summary>
    /// A source row active at <paramref name="updatedAt"/> for a role the target also holds then is dropped (the
    /// target's row wins); every other source row moves, so history is kept. One SaveChanges.
    /// </summary>
    Task ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
