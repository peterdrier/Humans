using Humans.Base.Interfaces;
using Humans.Monitor.Jobs;

namespace Humans.Monitor;

/// <summary>Monitor's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
public sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "monitor-drive-activity", typeof(DriveActivityMonitorJob), "0 * * * *");
    }
}
