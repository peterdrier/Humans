using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

/// <summary>
/// An individual ticket holder (issued ticket) from the vendor.
/// Multiple attendees can belong to a single order.
/// </summary>
public class TicketAttendee
{
    public Guid Id { get; init; }

    /// <summary>Vendor's issued ticket identifier. Unique.</summary>
    public string VendorTicketId { get; init; } = string.Empty;

    /// <summary>FK to the parent order.</summary>
    public Guid TicketOrderId { get; init; }

    /// <summary>Navigation to parent order.</summary>
    public TicketOrder TicketOrder { get; set; } = null!;

    /// <summary>Ticket holder's name.</summary>
    public string AttendeeName { get; set; } = string.Empty;

    /// <summary>Ticket holder's email (may not always be provided by vendor).</summary>
    public string? AttendeeEmail { get; set; }

    /// <summary>Auto-matched user by email. Null if no match or no email.</summary>
    public Guid? MatchedUserId { get; set; }

    /// <summary>Navigation to matched user.</summary>
    public User? MatchedUser { get; set; }

    /// <summary>Ticket type name (e.g. "Full Week", "Weekend Pass").</summary>
    public string TicketTypeName { get; set; } = string.Empty;

    /// <summary>Individual ticket price.</summary>
    public decimal Price { get; set; }

    /// <summary>Ticket status from vendor.</summary>
    public TicketAttendeeStatus Status { get; set; }

    /// <summary>Vendor event ID at time of sync (for future multi-event).</summary>
    public string VendorEventId { get; set; } = string.Empty;

    /// <summary>When this record was last synced from the vendor.</summary>
    public Instant SyncedAt { get; set; }
}
