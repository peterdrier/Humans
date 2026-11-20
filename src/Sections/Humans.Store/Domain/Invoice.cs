using NodaTime;

namespace Humans.Store.Domain;

internal sealed class Invoice
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string HoldedDocId { get; set; } = string.Empty;
    public string HoldedDocNumber { get; set; } = string.Empty;
    public Instant IssuedAt { get; set; }
    public Guid IssuedByUserId { get; set; }
    public string RequestPayload { get; set; } = string.Empty;
    public string ResponsePayload { get; set; } = string.Empty;

    /// <summary>Where booking the order's money onto this invoice in Holded stands; null is
    /// <see cref="InvoiceBookingState.NotBooked"/>.</summary>
    public InvoiceBookingState? BookingState { get; set; }

    /// <summary>Why booking is waiting or stopped, for the order page; null once booked.</summary>
    public string? BookingNote { get; set; }

    /// <summary>When Holded was last asked to email the invoice to the camp; null until it is sent.</summary>
    public Instant? SentAt { get; set; }
}
