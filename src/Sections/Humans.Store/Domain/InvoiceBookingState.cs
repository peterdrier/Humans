namespace Humans.Store.Domain;

internal enum InvoiceBookingState
{
    /// <summary>Not booked yet: invoices issued before booking existed, or an issuance whose booking never ran.</summary>
    NotBooked = 0,

    /// <summary>A charge's or refund's line isn't in Holded's feed yet; nothing was booked.</summary>
    WaitingForFeed = 1,

    /// <summary>The invoice is paid, the camp's 430 is at zero and every feed line of the order is reconciled.</summary>
    Booked = 2,

    /// <summary>Booking stopped on something a person has to look at; the note says what.</summary>
    Stopped = 3,
}
