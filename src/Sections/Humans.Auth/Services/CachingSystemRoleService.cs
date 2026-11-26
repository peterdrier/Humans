using Humans.Auth.Contracts;
using Humans.Base.Caching;
using Humans.Base.Constants;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Teams.Contracts;

namespace Humans.Auth.Services;

/// <summary>
/// Caches every <c>auth_role_assignments</c> row. <c>SystemRoleService</c> clears it right after each repository
/// write (merge and erasure included, which bypass this decorator), because the Board team sync inside
/// Assign/Revoke must read the new rows. This decorator also evicts after successful or uncertain writes;
/// explicit refusals and zero-row revocations preserve the warmed snapshot.
/// </summary>
internal sealed class CachingSystemRoleService
    : TrackedCache<Guid, SystemRoleRow>,
      ISystemRoleManagement
{
    public const string InnerServiceKey = "system-role-inner";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;

    public CachingSystemRoleService(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingSystemRoleService> logger)
        : base("Auth.SystemRoleRow", warmOnStartup: true, logger)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetActiveCountsByRoleAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        var now = _clock.GetCurrentInstant();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in AsReadOnlyDictionary.Values)
        {
            if (row.IsActiveAt(now))
            {
                counts.TryGetValue(row.RoleName, out var c);
                counts[row.RoleName] = c + 1;
            }
        }
        return counts;
    }

    protected override async Task WarmAllAsync(Action<Guid, SystemRoleRow> publish, CancellationToken ct)
    {
        var now = _clock.GetCurrentInstant();
        var (items, _) = await WithInnerAsync(inner => inner.GetFilteredAsync(
            roleFilter: null,
            activeOnly: false,
            page: 1,
            pageSize: int.MaxValue,
            now: now,
            ct: ct));

        foreach (var item in items)
        {
            var row = new SystemRoleRow(
                item.Id,
                item.UserId,
                item.RoleName,
                item.ValidFrom,
                item.ValidTo);
            publish(row.Id, row);
        }
    }

    public Task<(IReadOnlyList<SystemRoleSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetFilteredAsync(roleFilter, activeOnly, page, pageSize, now, ct));

    public Task<SystemRoleDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetByIdAsync(assignmentId, ct));

    public Task<IReadOnlyList<SystemRoleSummarySnapshot>> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetByUserIdAsync(userId, ct));

    public Task<SystemRoleResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId, string? notes,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.AssignRoleAsync(userId, roleName, assignerId, notes, ct), static result => result.Success);

    public Task<SystemRoleResult> RevokeRoleAsync(
        Guid assignmentId, Guid revokerId, string? notes,
        CancellationToken ct = default) =>
        MutateAsync(inner => inner.RevokeRoleAsync(assignmentId, revokerId, notes, ct), static result => result.Success);

    public Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        HasActiveRoleAsync(userId, RoleNames.Admin, cancellationToken);

    public Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default) =>
        HasActiveRoleAsync(userId, RoleNames.Board, cancellationToken);

    public Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        HasActiveRoleAsync(userId, TeamsRoles.TeamsAdmin, cancellationToken);

    public async Task<bool> HasActiveRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default) =>
        (await ActiveRowsAsync(cancellationToken)).Any(row =>
            row.UserId == userId && string.Equals(row.RoleName, roleName, StringComparison.Ordinal));

    public async Task<bool> HasAnyActiveAssignmentAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await ActiveRowsAsync(cancellationToken)).Any(row => row.UserId == userId);

    public async Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(CancellationToken cancellationToken = default) =>
        (await ActiveRowsAsync(cancellationToken)).Select(row => row.UserId).Distinct().ToList();

    public Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RevokeAllActiveAsync(userId, ct), static count => count > 0);

    public async Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(string roleName, CancellationToken ct = default) =>
        (await ActiveRowsAsync(ct))
            .Where(row => string.Equals(row.RoleName, roleName, StringComparison.Ordinal))
            .Select(row => row.UserId)
            .Distinct()
            .ToList();

    public async Task<IReadOnlyList<SystemRoleSnapshot>> GetActiveForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        var now = _clock.GetCurrentInstant();
        return AsReadOnlyDictionary.Values
            .Where(row => row.UserId == userId && row.IsActiveAt(now))
            .Select(row => new SystemRoleSnapshot(row.RoleName, row.ValidTo))
            .ToList();
    }

    /// <summary>Rows carry canonical role names, so callers pass a role's current name.</summary>
    private async Task<IEnumerable<SystemRoleRow>> ActiveRowsAsync(CancellationToken ct)
    {
        await EnsureWarmedAsync(ct);
        var now = _clock.GetCurrentInstant();
        return AsReadOnlyDictionary.Values.Where(row => row.IsActiveAt(now));
    }

    private async Task<TResult> MutateAsync<TResult>(
        Func<ISystemRoleManagement, Task<TResult>> action, Func<TResult, bool> changed)
    {
        var invalidate = true;
        try
        {
            var result = await WithInnerAsync(action);
            invalidate = changed(result);
            return result;
        }
        finally
        {
            if (invalidate) Clear();
        }
    }

    private async Task<TResult> WithInnerAsync<TResult>(Func<ISystemRoleManagement, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider
            .GetRequiredKeyedService<ISystemRoleManagement>(InnerServiceKey);
        return await action(inner);
    }
}
