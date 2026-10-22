using Humans.Base.Interfaces;
using Humans.Preapprovals.Jobs;

namespace Humans.Preapprovals;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            PreapprovalRemindersJob.JobId, typeof(PreapprovalRemindersJob), "0 5 * * *");
    }
}
