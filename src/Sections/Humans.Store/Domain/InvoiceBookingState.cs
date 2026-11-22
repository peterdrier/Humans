namespace Humans.Store.Domain;

internal enum InvoiceBookingState
{
    /// <summary>Not booked yet: invoices issued before booking existed, or an issuance whose booking never ran.</summary>
    NotBooked = 0,

    /// <summary>A charge's or refund's line isn't in Holded's feed yet; nothing was booked.</summary>
    WaitingForFeed = 1,

    /// <summary>The invoice is paid, the camp's 430 is at zero and every feed line of the order is reconciled.</summary>
    Booked = 2,

    /// <summary>Booking stopped before posting anything, on something a person has to fix (a setting, a manual
    /// payment); the note says what, and Retry runs it again.</summary>
    Stopped = 3,

    /// <summary>Booking stopped after posting to Holded; the note lists what was posted. Never rerun, since a rerun
    /// could post a collection or refund payment twice: the rest is done in Holded by hand.</summary>
    NeedsReview = 4,
}
