using Humans.Base.Interfaces;
using Humans.Holded.Jobs;

namespace Humans.Holded;

/// <summary>Holded's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Nightly sweep of the ledger mirror's trailing window (the daybook every account reads).
        yield return new RecurringJobDescriptor(
            "holded-sync", typeof(HoldedSyncJob), "0 3 * * *");
    }
}
