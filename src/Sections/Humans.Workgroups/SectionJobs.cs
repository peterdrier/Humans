using Humans.Base.Interfaces;
using Humans.Workgroups.Jobs;

namespace Humans.Workgroups;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            WorkgroupRhythmJob.JobId, typeof(WorkgroupRhythmJob), "0 6 * * *");
    }
}
