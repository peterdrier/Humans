using Humans.Base.Interfaces;
using Humans.Monitor.Jobs;

namespace Humans.Monitor;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "monitor-drive-activity", typeof(DriveActivityMonitorJob), "0 * * * *");
    }
}
