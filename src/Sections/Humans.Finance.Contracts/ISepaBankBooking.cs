using Humans.Base.Attributes;

namespace Humans.Finance.Contracts;

/// <summary>The body of <c>SepaBankBookingJob</c>: a public job type cannot take Finance's internal admin service.
/// Nothing else calls it.</summary>
public interface ISepaBankBooking
{
    /// <summary>Books every unbooked transfer whose outgoing Sabadell line has appeared, and retries the reconcile for
    /// every booked-but-unreconciled one.</summary>
    [ExternalWrite]
    Task RunAsync(CancellationToken ct = default);
}
