using Humans.Base.Interfaces;
using Humans.Preapprovals.Jobs;

namespace Humans.Preapprovals;

/// <summary>Preapprovals' one recurring job: the nightly 14-day and 3-day deadline reminders.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            PreapprovalRemindersJob.JobId, typeof(PreapprovalRemindersJob), "0 5 * * *");
    }
}
