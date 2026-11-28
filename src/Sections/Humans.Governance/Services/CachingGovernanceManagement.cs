using Humans.Base.Caching;
using Humans.Governance.Contracts;
using Humans.Governance.Services.Dtos;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Governance.Services;

/// <summary>
/// Holds each Board member's count of open applications they have not voted on, which the voting
/// pill reads on every page. <see cref="ApplicationDecisionService"/> clears it after any submit,
/// vote, decision, withdrawal or merge.
/// </summary>
internal sealed class CachingGovernanceManagement(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingGovernanceManagement> logger)
    : TrackedCache<Guid, int>("Governance.UnvotedApplicationCounts", warmOnStartup: false, logger),
      IGovernanceManagement
{
    public const string InnerServiceKey = "governance-management-inner";

    public async Task<int> GetUnvotedApplicationCountAsync(
        Guid boardMemberUserId, CancellationToken ct = default) =>
        await GetAsync(boardMemberUserId, ct);

    protected override async ValueTask<int> LoadRowAsync(Guid key, CancellationToken ct) =>
        await WithInnerAsync(inner => inner.GetUnvotedApplicationCountAsync(key, ct));

    public Task<IReadOnlyList<UserApplicationSnapshot>> GetUserApplicationsAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetUserApplicationsAsync(userId, ct));

    public Task<SubmittedApplicationSnapshot?> GetSubmittedApplicationForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetSubmittedApplicationForUserAsync(userId, ct));

    public Task<IReadOnlyList<ApplicationRenewalReminderCandidate>> GetExpiringApplicationsNeedingReminderAsync(
        LocalDate today, LocalDate reminderThreshold, CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetExpiringApplicationsNeedingReminderAsync(today, reminderThreshold, ct));

    public Task<IReadOnlySet<(Guid UserId, MembershipTier Tier)>> GetPendingApplicationUserTiersAsync(
        CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetPendingApplicationUserTiersAsync(ct));

    public Task MarkRenewalReminderSentAsync(
        Guid applicationId, Instant sentAt, CancellationToken ct = default) =>
        WithInnerAsync(async inner =>
        {
            await inner.MarkRenewalReminderSentAsync(applicationId, sentAt, ct);
            return true;
        });

    public Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetAdminStatsAsync(ct));

    public Task<int> GetPendingApplicationCountAsync(CancellationToken ct = default) =>
        WithInnerAsync(inner => inner.GetPendingApplicationCountAsync(ct));

    private async Task<T> WithInnerAsync<T>(Func<IGovernanceManagement, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredKeyedService<IGovernanceManagement>(InnerServiceKey));
    }
}
