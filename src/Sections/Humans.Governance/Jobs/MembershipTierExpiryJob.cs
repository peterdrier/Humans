using Hangfire;
using NodaTime;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Services;

namespace Humans.Governance.Jobs;

/// <summary>
/// System-team membership does not wait on this job: Teams' sync reads the date-filtered active
/// approvals, so a lapsed term leaves the team on the first sync after the date turns.
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class MembershipTierExpiryJob(
    IGovernanceManagement governance,
    IHumansMetrics metrics,
    ILogger<MembershipTierExpiryJob> logger,
    IClock clock) : IRecurringJob
{
    private static readonly MembershipTier[] ExpiringTiers = [MembershipTier.Asociado, MembershipTier.Colaborador];

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var today = clock.GetCurrentInstant().InUtc().Date;
            var total = 0;
            foreach (var tier in ExpiringTiers)
                total += (await governance.ExpireMembershipTiersAsync(tier, today, cancellationToken)).Count;

            metrics.RecordJobRun("membership_tier_expiry", "success");
            logger.LogInformation("Membership tier expiry finished; downgraded {Count} member(s)", total);
        }
        catch (Exception ex)
        {
            metrics.RecordJobRun("membership_tier_expiry", "failure");
            logger.LogError(ex, "Error during membership tier expiry");
            throw;
        }
    }
}
