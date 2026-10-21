using Hangfire;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Governance.Contracts;
using Humans.Governance.Services;
using Humans.Users.Contracts;

namespace Humans.Governance.Jobs;

/// <summary>
/// Daily Colaborador / Asociado term expiry: every member whose tier has no Approved application
/// still in term drops to their other in-term tier, or to Volunteer, with one
/// <see cref="AuditAction.TierDowngraded"/> entry each. The schedule lives in <c>SectionJobs.cs</c>.
/// </summary>
/// <remarks>
/// System-team membership does not wait on this job: Teams' sync reads the date-filtered active
/// approvals, so a lapsed term leaves the team on the first sync after the date turns.
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
internal sealed class MembershipTierExpiryJob(
    IGovernanceManagement governance,
    IUserService userService,
    IAuditLogService auditLogService,
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
                total += await ExpireTierAsync(tier, today, cancellationToken);

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

    private async Task<int> ExpireTierAsync(MembershipTier tier, LocalDate today, CancellationToken ct)
    {
        var downgrades = await governance.ExpireMembershipTiersAsync(tier, today, ct);
        if (downgrades.Count == 0)
            return 0;

        var usersById = await userService.GetUserInfosAsync(downgrades.Select(d => d.UserId).ToList(), ct);
        foreach (var (userId, newTier) in downgrades)
        {
            var displayName = usersById.TryGetValue(userId, out var u) ? u.BurnerName : "Unknown";
            await auditLogService.LogAsync(
                AuditAction.TierDowngraded, "Profile", userId,
                $"Membership tier changed to {newTier} for {displayName} due to {tier} term expiry",
                nameof(MembershipTierExpiryJob),
                relatedEntityId: userId, relatedEntityType: nameof(User));
        }
        return downgrades.Count;
    }
}
