using Humans.Base.Interfaces;
using Humans.Gate.Jobs;

namespace Humans.Gate;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "gate-retention", typeof(GateRetentionJob), "45 3 * * *");
    }
}
