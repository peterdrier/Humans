using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Tickets.Contracts;

/// <summary>
/// Best-effort mirror of a gate admission to the vendor. Gate's own scan events stay the dedupe authority; a
/// failed mirror is not a failed admission.
/// </summary>
public interface ITicketVendorMirror : IApplicationService
{
    /// <summary>Not idempotent: each call creates a vendor record, so callers must never retry.</summary>
    [ExternalWrite]
    Task CreateCheckInAsync(
        string vendorTicketId, Instant occurredAt, CancellationToken ct = default);
}
