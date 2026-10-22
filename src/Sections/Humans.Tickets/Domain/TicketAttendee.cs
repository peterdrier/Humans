using NodaTime;
using Humans.Tickets.Contracts;

namespace Humans.Tickets.Domain;

internal sealed class TicketAttendee
{
    public Guid Id { get; init; }

    public string VendorTicketId { get; init; } = string.Empty;

    public Guid TicketOrderId { get; init; }

    public TicketOrder TicketOrder { get; set; } = null!;

    public string AttendeeName { get; set; } = string.Empty;

    public string? AttendeeEmail { get; set; }

    /// <summary>While set, sync must never write <see cref="AttendeeName"/> or <see cref="AttendeeEmail"/> again.</summary>
    public Instant? PiiErasedAt { get; set; }

    /// <summary>The scannable code on the ticket and its QR, distinct from <see cref="VendorTicketId"/>. The vendor has no
    /// lookup by barcode, so every resolution runs against synced rows.</summary>
    public string? Barcode { get; set; }

    public Guid? MatchedUserId { get; set; }

    public string TicketTypeName { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public TicketAttendeeStatus Status { get; set; }

    /// <summary>
    /// A checked-in ticket stays <see cref="TicketAttendeeStatus.Valid"/>. Write-once: the earliest scan, never
    /// cleared by later syncs.
    /// </summary>
    public Instant? CheckedInAt { get; set; }

    public string VendorEventId { get; set; } = string.Empty;

    public Instant SyncedAt { get; set; }
}
