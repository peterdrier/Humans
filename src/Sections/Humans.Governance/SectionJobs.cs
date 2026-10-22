using Humans.Base.Interfaces;
using Humans.Governance.Jobs;

namespace Humans.Governance;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // 90-day horizon.
        yield return new RecurringJobDescriptor(
            "governance-term-renewal-reminder", typeof(TermRenewalReminderJob), "0 5 * * 1");

        // Tier term expiry. Five minutes after the midnight UTC system-team sync, which has
        // already dropped the lapsed members from their team; the sync reads the dated
        // approvals, so it never waits on this.
        yield return new RecurringJobDescriptor(
            "governance-membership-tier-expiry", typeof(MembershipTierExpiryJob), "5 0 * * *");

        // Close lapsed assembly votes and send the T-24h ballot reminder. Hourly is enough:
        // the service closes a lapsed vote inline on any request that sees the deadline, so
        // this only has to catch votes nobody looked at.
        yield return new RecurringJobDescriptor(
            "governance-assembly-vote-lapse", typeof(AssemblyVoteLapseJob), "0 * * * *");
    }
}
