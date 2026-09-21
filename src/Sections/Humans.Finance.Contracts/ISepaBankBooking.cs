using Humans.Base.Attributes;

namespace Humans.Finance.Contracts;

/// <summary>
/// The SEPA bank-line sweep, as one call. <c>SepaBankBookingJob</c> is the Hangfire shim; this is
/// the body — the same seam <c>IHoldedNightlySync</c> is for <c>HoldedSyncJob</c>, because Hangfire
/// needs a public concrete job type and a public job type cannot take Finance's internal
/// <c>IHoldedFinanceAdminService</c>. Nothing outside the job calls it
/// (nobodies-collective/Humans#1185).
/// </summary>
public interface ISepaBankBooking
{
    /// <summary>Books every unbooked SEPA transfer whose outgoing Sabadell line has appeared, and
    /// retries the reconcile for every booked-but-unreconciled one.</summary>
    [ExternalWrite]
    Task RunAsync(CancellationToken ct = default);
}
