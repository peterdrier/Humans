using Humans.Auth.Contracts;
using Humans.Base.Caching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Auth.Services;

/// <summary>
/// Singleton caching decorator for <see cref="IRoleAssignmentManagement"/>.
/// Caches the full set of <c>role_assignments</c> rows so
/// <see cref="GetActiveCountsByRoleAsync"/> and <see cref="GetActiveForUserAsync"/>
/// are derived in memory at any clock instant. Cleared wholesale by
/// <c>RoleAssignmentService</c> right after each of its repository writes, merge fold
/// (<c>IUserMerge.ReassignAsync</c>) and GDPR erasure included — those two reach the
/// inner service directly, not through this decorator, and the Board team sync inside
/// Assign/End must read the new row set, so the eviction cannot wait for the
/// pass-through to return. No EF interceptor — all writes go through
/// <c>RoleAssignmentService</c>.
/// </summary>
/// <remarks>
/// Warmed on startup via the <see cref="TrackedCache{TKey,TValue}"/> base, wholesale
/// flush on writes, stats exposed via <c>/Debug/CacheStats</c>. Every other surface
/// method passes through to the inner service via a freshly resolved scope.
/// </remarks>
internal sealed class CachingRoleAssignmentService
    : TrackedCache<Guid, RoleAssignmentRow>,
      IRoleAssignmentManagement
{
    /// <summary>
    /// DI service key under which the undecorated (inner)
    /// <see cref="IRoleAssignmentManagement"/> is registered. The Singleton
    /// decorator resolves the Scoped inner per-call via
    /// <see cref="IServiceScopeFactory"/>.
    /// </summary>
    public const string InnerServiceKey = "role-assignment-inner";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;

    public CachingRoleAssignmentService(
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingRoleAssignmentService> logger)
        : base("Auth.RoleAssignmentRow", warmOnStartup: true, logger)
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

    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var now = _clock.GetCurrentInstant();
        var (items, _) = await WithInner(inner => inner.GetFilteredAsync(
            roleFilter: null,
            activeOnly: false,
            page: 1,
            pageSize: int.MaxValue,
            now: now,
            ct: ct));

        foreach (var item in items)
        {
            var row = new RoleAssignmentRow(
                item.Id,
                item.UserId,
                item.RoleName,
                item.ValidFrom,
                item.ValidTo);
            Set(row.Id, row);
        }
    }

    public Task<(IReadOnlyList<RoleAssignmentSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default) =>
        WithInner(inner => inner.GetFilteredAsync(roleFilter, activeOnly, page, pageSize, now, ct));

    public Task<RoleAssignmentDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetByIdAsync(assignmentId, ct));

    public Task<IReadOnlyList<RoleAssignmentSummarySnapshot>> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetByUserIdAsync(userId, ct));

    public Task<RoleAssignmentResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId, string? notes,
        CancellationToken ct = default) =>
        WithInner(inner => inner.AssignRoleAsync(userId, roleName, assignerId, notes, ct));

    public Task<RoleAssignmentResult> EndRoleAsync(
        Guid assignmentId, Guid enderId, string? notes,
        CancellationToken ct = default) =>
        WithInner(inner => inner.EndRoleAsync(assignmentId, enderId, notes, ct));

    public Task<bool> IsUserAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.IsUserAdminAsync(userId, cancellationToken));

    public Task<bool> IsUserBoardMemberAsync(Guid userId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.IsUserBoardMemberAsync(userId, cancellationToken));

    public Task<bool> IsUserTeamsAdminAsync(Guid userId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.IsUserTeamsAdminAsync(userId, cancellationToken));

    public Task<bool> HasActiveRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.HasActiveRoleAsync(userId, roleName, cancellationToken));

    public Task<bool> HasAnyActiveAssignmentAsync(Guid userId, CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.HasAnyActiveAssignmentAsync(userId, cancellationToken));

    public Task<IReadOnlyList<Guid>> GetUserIdsWithActiveAssignmentsAsync(CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.GetUserIdsWithActiveAssignmentsAsync(cancellationToken));

    public Task<int> RevokeAllActiveAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.RevokeAllActiveAsync(userId, ct));

    public Task<IReadOnlyList<Guid>> GetActiveUserIdsInRoleAsync(string roleName, CancellationToken ct = default) =>
        WithInner(inner => inner.GetActiveUserIdsInRoleAsync(roleName, ct));

    public async Task<IReadOnlyList<RoleAssignmentSnapshot>> GetActiveForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        var now = _clock.GetCurrentInstant();
        return AsReadOnlyDictionary.Values
            .Where(row => row.UserId == userId && row.IsActiveAt(now))
            .OrderBy(row => row.RoleName, StringComparer.Ordinal)
            .Select(row => new RoleAssignmentSnapshot(row.RoleName, row.ValidTo))
            .ToList();
    }

    private async Task<TResult> WithInner<TResult>(Func<IRoleAssignmentManagement, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider
            .GetRequiredKeyedService<IRoleAssignmentManagement>(InnerServiceKey);
        return await action(inner);
    }
}
