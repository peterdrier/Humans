using Humans.Base.Authorization;
using Humans.Base.Caching;
using Humans.Gdpr.Contracts;
using Humans.Permissions.Contracts;
using Humans.Roles.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Roles.Services;

/// <summary>
/// Every role with its holders, keyed by role id; every write clears it. Serves Roles as a permission source, so
/// under the DI rule on <see cref="IOwnerKind"/> it injects nothing that reaches the permission service.
/// </summary>
internal sealed class CachingRoleService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingRoleService> logger)
    : TrackedCache<Guid, RoleInfo>("Roles.Role", warmOnStartup: true, logger),
      IRoleManagementService, IPermissionSource, IUserDataContributor, IUserMerge
{
    public const string InnerServiceKey = "roles-inner";

    public async Task<IReadOnlyList<RoleInfo>> GetRolesAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return [.. AsReadOnlyDictionary.Values];
    }

    public async Task<RoleInfo?> GetRoleAsync(Guid roleId, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return TryGet(roleId, out var role) ? role : null;
    }

    public async Task<IReadOnlyList<Holding>> GetHoldingsAsync(Guid userId, CancellationToken ct = default) =>
        (await GetRolesAsync(ct))
            .Where(r => r.Holders.Any(h => h.UserId == userId))
            .Select(r => new Holding(r.Owner, r.IsCoordinator, r.PermissionKeys, r.Name))
            .ToList();

    public Task<bool> CanManageAsync(Owner owner, Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.CanManageAsync(owner, userId, ct));

    public Task<RoleResult> CreateAsync(Owner owner, RoleInput input, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.CreateAsync(owner, input, actorUserId, ct));

    public Task<RoleResult> UpdateAsync(Guid roleId, RoleInput input, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UpdateAsync(roleId, input, actorUserId, ct));

    public Task<RoleResult> DeleteAsync(Guid roleId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.DeleteAsync(roleId, actorUserId, ct));

    public Task<RoleResult> AssignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.AssignAsync(roleId, userId, actorUserId, ct));

    public Task<RoleResult> UnassignAsync(Guid roleId, Guid userId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.UnassignAsync(roleId, userId, actorUserId, ct));

    public Task RemoveAsync(Owner owner, Guid userId, Guid actorUserId, CancellationToken ct = default) =>
        MutateAsync(inner => inner.RemoveAsync(owner, userId, actorUserId, ct));

    // Static: the erasure-coverage architecture test reads it from an uninitialized instance.
    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal) { [RoleService.ExportSection] = null };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        WithInnerAsync(inner => inner.ContributeForUserAsync(userId, ct));

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        MutateAsync(inner => inner.EraseForUserAsync(userId, ct));

    public Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct) =>
        MutateAsync(inner => inner.ReassignAsync(mergedFromUserId, mergedToUserId, actorUserId, now, ct));

    protected override async Task WarmAllAsync(Action<Guid, RoleInfo> publish, CancellationToken ct)
    {
        foreach (var role in await WithInnerAsync(inner => inner.GetRolesAsync(ct)))
            publish(role.Id, role);
    }

    // A write can commit before its audit fails: always evict, and let the failure reach the caller.
    private Task MutateAsync(Func<IRoleManagementService, Task> work) =>
        MutateAsync(async inner =>
        {
            await work(inner);
            return true;
        });

    private async Task<T> MutateAsync<T>(Func<IRoleManagementService, Task<T>> work)
    {
        try
        {
            return await WithInnerAsync(work);
        }
        finally
        {
            Clear();
        }
    }

    private async Task<T> WithInnerAsync<T>(Func<IRoleManagementService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredKeyedService<IRoleManagementService>(InnerServiceKey));
    }
}
