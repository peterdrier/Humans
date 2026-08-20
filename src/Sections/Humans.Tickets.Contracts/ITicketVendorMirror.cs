using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Best-effort mirror of a gate admission back to the ticket vendor, so the vendor
/// dashboard and vendor check-in app stay consistent with what happened at the gate.
/// </summary>
/// <remarks>
/// Driven by <c>GateVendorCheckInJob</c>, which moved to <c>Humans.Gate/Contracts/</c> at G5
/// lane 5b-3 (nobodies-collective/Humans#866) — this is now a plain section-to-section call
/// through a public contract, not a Base consumer. It goes through
/// Tickets rather than straight at the vendor port for the reason
/// <see cref="ITicketDiscountCodes"/> does. This is a write, so it is not on
/// <see cref="ITicketServiceRead"/>. The Gate section's own <c>gate_scan_events</c>
/// remains the dedupe authority; a failed mirror is not a failed admission.
/// </remarks>
public interface ITicketVendorMirror : IApplicationService
{
    /// <summary>Records a check-in for an issued ticket at the vendor. Safe to retry.</summary>
    [ExternalWrite]
    Task CreateCheckInAsync(
        string vendorTicketId, Instant occurredAt, CancellationToken ct = default);
}
