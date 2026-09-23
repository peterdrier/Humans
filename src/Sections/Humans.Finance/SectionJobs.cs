using Humans.Base.Interfaces;
using Humans.Finance.Jobs;

namespace Humans.Finance;

/// <summary>Finance's recurring jobs. Discovered by Shell — nothing names it, so it needs no
/// section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // The outgoing Sabadell line lands 6-50 h after a payout file is generated, so hourly buys
        // nothing; :17 rather than :00 keeps it out of the on-the-hour stampede.
        yield return new RecurringJobDescriptor(
            "sepa-bank-booking", typeof(SepaBankBookingJob), "17 */2 * * *");
    }
}
