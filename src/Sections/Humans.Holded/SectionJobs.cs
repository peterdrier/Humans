using Humans.Base.Interfaces;
using Humans.Holded.Jobs;

namespace Humans.Holded;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "holded-sync", typeof(HoldedSyncJob), "0 3 * * *");
    }
}
