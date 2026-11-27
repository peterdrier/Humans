using Humans.Base.Caching;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Holds every non-Volunteer tier, which the tier pill reads on each render. Writers of
/// <c>membership_tiers</c> (the expiry job, approvals, merges) call <see cref="TrackedCache{TKey,TValue}.Clear"/>.
/// Term reads go to the database: they are rare and date-dependent.
/// </summary>
internal sealed class CachingGovernanceService(
    IServiceScopeFactory scopeFactory,
    IUserService users,
    ILogger<CachingGovernanceService> logger)
    : TrackedCache<Guid, MembershipTier>("Governance.MembershipTiers", warmOnStartup: true, logger),
      IGovernanceService
{
    public const string InnerServiceKey = "governance-inner";

    public async Task<MembershipTier> GetMembershipTierAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await users.GetUserInfoAsync(userId, ct);
        await EnsureWarmedAsync(ct);
        return TryGet(info?.Id ?? userId, out var tier) ? tier : MembershipTier.Volunteer;
    }

    public async Task<IReadOnlyDictionary<Guid, MembershipTier>> GetMembershipTiersAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary.ToDictionary();
    }

    public Task<IReadOnlyList<Guid>> GetActiveApprovedTierUserIdsAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetActiveApprovedTierUserIdsAsync(tier, today, ct));

    public Task<bool> HasActiveApprovedTierAsync(
        Guid userId, MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.HasActiveApprovedTierAsync(userId, tier, today, ct));

    protected override async Task WarmAllAsync(Action<Guid, MembershipTier> publish, CancellationToken ct)
    {
        foreach (var (userId, tier) in await WithInnerAsync(inner => inner.GetMembershipTiersAsync(ct)))
            publish(userId, tier);
    }

    private async Task<T> WithInnerAsync<T>(Func<IGovernanceService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredKeyedService<IGovernanceService>(InnerServiceKey));
    }
}
