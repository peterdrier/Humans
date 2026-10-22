using Humans.Base.Interfaces;
using Humans.Finance.Jobs;

namespace Humans.Finance;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // The Sabadell line lands 6-50 h after a file is generated, so hourly buys nothing; :17 avoids the on-the-hour
        // stampede.
        yield return new RecurringJobDescriptor(
            "sepa-bank-booking", typeof(SepaBankBookingJob), "17 */2 * * *");

        // Ahead of Holded's 03:00 ledger sweep; the shared "holded-api" lock keeps them apart if this runs long.
        yield return new RecurringJobDescriptor(
            "holded-doc-sync", typeof(HoldedDocSyncJob), "45 2 * * *");
    }
}
