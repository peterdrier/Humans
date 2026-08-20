using Humans.Base.Interfaces;
using Humans.Gate.Jobs;

namespace Humans.Gate;

/// <summary>
/// Gate's recurring jobs. Discovered by Shell — nothing names it, so it needs no section
/// prefix. <c>GateVendorCheckInJob</c> is not here — it is enqueued fire-and-forget on admit
/// (<see cref="Controllers.GateController"/>), not a recurring job.
/// </summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Purge gate scan events past the retention window (Gate:RetentionDays).
        yield return new RecurringJobDescriptor(
            "gate-retention", typeof(GateRetentionJob), "45 3 * * *");
    }
}
