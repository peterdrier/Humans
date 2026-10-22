namespace Humans.Tickets.Contracts;

public enum ParticipationStatus
{
    NotAttending = 0,

    Ticketed = 1,

    /// <summary>Checked in at the gate.</summary>
    Attended = 2,

    /// <summary>Had a ticket but did not attend.</summary>
    NoShow = 3,
}
