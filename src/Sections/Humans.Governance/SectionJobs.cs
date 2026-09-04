using Humans.Base.Interfaces;
using Humans.Governance.Jobs;

namespace Humans.Governance;

/// <summary>Governance's recurring jobs. Discovered by Shell.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // 90-day horizon.
        yield return new RecurringJobDescriptor(
            "governance-term-renewal-reminder", typeof(TermRenewalReminderJob), "0 5 * * 1");
    }
}
