using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Auth;

/// <summary>
/// Singleton caching decorator for <see cref="IRoleAssignmentService"/>.
/// Caches the full set of <c>role_assignments</c> rows so cross-section reads
/// such as <see cref="GetActiveCountsByRoleAsync"/> can be derived in memory
/// at any clock instant. Invalidated wholesale by
/// <c>RoleAssignmentSaveChangesInterceptor</c> after any persisted write
/// to <c>role_assignments</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>CachingLegalDocumentSyncService</c>: warmed on startup via the
/// <see cref="TrackedCache{TKey,TValue}"/> base, wholesale flush on writes,
/// stats exposed via <c>/Admin/CacheStats</c>. Reads served from cache:
/// <see cref="GetActiveCountsByRoleAsync"/>,
/// <see cref="GetActiveForUserAsync"/>. Every other surface method passes
/// through to the inner service via a freshly resolved scope. Other read
/// methods can be migrated to cached derivations incrementally as new
/// callers arrive — the cache shape (<see cref="RoleAssignmentRow"/>) holds
/// the fields needed to answer "active-at-now" predicates by user or role.
/// </remarks>
public sealed class CachingRoleAssignmentService
    : TrackedCache<Guid, RoleAssignmentRow>,
      IRoleAssignmentService,
      IRoleAssignmentCacheInvalidator
{
    /// <summary>
    /// DI service key under which the undecorated (inner)
    /// <see cref="IRoleAssignmentService"/> is registered. The Singleton
    /// decorator resolves the Scoped inner per-call via
    /// <see cref="IServiceScopeFactory"/>.
    /// </summary>
    public const string InnerServiceKey = "role-assignment-inner";

    private readonly IRoleAssignmentRepository _repository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;

    public CachingRoleAssignmentService(
        IRoleAssignmentRepository repository,
        IServiceScopeFactory scopeFactory,
        IClock clock,
        ILogger<CachingRoleAssignmentService> logger)
        : base("Auth.RoleAssignmentRow", warmOnStartup: true, logger)
    {
        _repository = repository;
        _scopeFactory = scopeFactory;
        _clock = clock;
    }

    // ==========================================================================
    // Reads served from cache
    // ==========================================================================

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

    // ==========================================================================
    // IRoleAssignmentCacheInvalidator
    // ==========================================================================

    public void InvalidateAll() => Clear();

    // ==========================================================================
    // Warm / load
    // ==========================================================================

    protected override async Task WarmAllAsync(CancellationToken ct)
    {
        var entities = await _repository.GetAllRowsForCacheAsync(ct);
        foreach (var ra in entities)
            Set(ra.Id, new RoleAssignmentRow(ra.Id, ra.UserId, ra.RoleName, ra.ValidFrom, ra.ValidTo));
    }

    // ==========================================================================
    // Pass-through to inner service (scope-resolved per call)
    // ==========================================================================

    public Task<bool> HasOverlappingAssignmentAsync(
        Guid userId, string roleName, Instant validFrom, Instant? validTo = null,
        CancellationToken cancellationToken = default) =>
        WithInner(inner => inner.HasOverlappingAssignmentAsync(userId, roleName, validFrom, validTo, cancellationToken));

    public Task<(IReadOnlyList<RoleAssignmentSummarySnapshot> Items, int TotalCount)> GetFilteredAsync(
        string? roleFilter, bool activeOnly, int page, int pageSize, Instant now,
        CancellationToken ct = default) =>
        WithInner(inner => inner.GetFilteredAsync(roleFilter, activeOnly, page, pageSize, now, ct));

    public Task<RoleAssignmentDetailSnapshot?> GetByIdAsync(Guid assignmentId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetByIdAsync(assignmentId, ct));

    public Task<IReadOnlyList<RoleAssignmentSummarySnapshot>> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        WithInner(inner => inner.GetByUserIdAsync(userId, ct));

    public Task<OnboardingResult> AssignRoleAsync(
        Guid userId, string roleName, Guid assignerId, string? notes,
        CancellationToken ct = default) =>
        WithInner(inner => inner.AssignRoleAsync(userId, roleName, assignerId, notes, ct));

    public Task<OnboardingResult> EndRoleAsync(
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

    public void InvalidateClaimsCacheForUser(Guid userId)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IRoleAssignmentService>(InnerServiceKey);
        inner.InvalidateClaimsCacheForUser(userId);
    }

    public void InvalidateNavBadgeCache()
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<IRoleAssignmentService>(InnerServiceKey);
        inner.InvalidateNavBadgeCache();
    }

    // Implemented directly on the decorator (it IS the cache invalidator);
    // no need to flow through the inner service.
    void IRoleAssignmentService.InvalidateRoleAssignmentCache() => InvalidateAll();

    // ==========================================================================
    // Inner-service resolution
    // ==========================================================================

    private async Task<TResult> WithInner<TResult>(Func<IRoleAssignmentService, Task<TResult>> action)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider
            .GetRequiredKeyedService<IRoleAssignmentService>(InnerServiceKey);
        return await action(inner);
    }
}
