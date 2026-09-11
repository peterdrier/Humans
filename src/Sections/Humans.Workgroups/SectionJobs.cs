using Humans.Base.Interfaces;
using Humans.Workgroups.Jobs;

namespace Humans.Workgroups;

/// <summary>Workgroups' one recurring job: the daily reporting-rhythm pass of design §13.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Early morning, before anyone reads their notifications over coffee.
        yield return new RecurringJobDescriptor(
            WorkgroupRhythmJob.JobId, typeof(WorkgroupRhythmJob), "0 6 * * *");
    }
}
