namespace Humans.Domain.Enums;

/// <summary>
/// Status of a user's participation in a yearly event.
/// </summary>
public enum ParticipationStatus
{
    /// <summary>
    /// User has declared they are not attending this year.
    /// </summary>
    NotAttending = 0,

    /// <summary>
    /// User has a valid ticket for the event.
    /// </summary>
    Ticketed = 1,

    /// <summary>
    /// User attended the event (checked in).
    /// </summary>
    Attended = 2,

    /// <summary>
    /// User had a ticket but did not attend (post-event derivation).
    /// </summary>
    NoShow = 3,
}
