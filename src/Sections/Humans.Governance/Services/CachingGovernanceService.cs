using Humans.Base.Caching;
using Humans.Governance.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Holds each looked-up member's tier, Volunteer included, which the tier pill reads on each render. Writers
/// of <c>membership_tiers</c> (the expiry job, approvals, merges) call <see cref="TrackedCache{TKey,TValue}.Clear"/>.
/// The full tier list and term reads go to the database: they are rare, and term reads are date-dependent.
/// </summary>
internal sealed class CachingGovernanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingGovernanceService> logger)
    : TrackedCache<Guid, MembershipTier>("Governance.MembershipTiers", warmOnStartup: false, logger),
      IGovernanceService
{
    public const string InnerServiceKey = "governance-inner";

    public async Task<MembershipTier> GetMembershipTierAsync(Guid userId, CancellationToken ct = default) =>
        await GetAsync(userId, ct);

    protected override async ValueTask<MembershipTier> LoadRowAsync(Guid key, CancellationToken ct) =>
        await WithInnerAsync(inner => inner.GetMembershipTierAsync(key, ct));

    public Task<IReadOnlyDictionary<Guid, MembershipTier>> GetMembershipTiersAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetMembershipTiersAsync(ct));

    public Task<IReadOnlyList<Guid>> GetActiveApprovedTierUserIdsAsync(
        MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetActiveApprovedTierUserIdsAsync(tier, today, ct));

    public Task<bool> HasActiveApprovedTierAsync(
        Guid userId, MembershipTier tier, LocalDate today, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.HasActiveApprovedTierAsync(userId, tier, today, ct));

    private async Task<T> WithInnerAsync<T>(Func<IGovernanceService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredKeyedService<IGovernanceService>(InnerServiceKey));
    }
}
