using Humans.Base.Interfaces;
using Humans.Governance.Jobs;

namespace Humans.Governance;

/// <summary>Governance's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Send term renewal reminders to Colaboradors/Asociados whose terms expire within 90 days.
        yield return new RecurringJobDescriptor(
            "governance-term-renewal-reminder", typeof(TermRenewalReminderJob), "0 5 * * 1");
    }
}
