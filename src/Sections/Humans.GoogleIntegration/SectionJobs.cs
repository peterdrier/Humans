using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Jobs;

namespace Humans.GoogleIntegration;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "google-resource-reconciliation", typeof(GoogleResourceReconciliationJob), "0 3 * * *");

        yield return new RecurringJobDescriptor(
            "google-sync-outbox-process", typeof(ProcessGoogleSyncOutboxJob), "*/10 * * * *");
    }
}
