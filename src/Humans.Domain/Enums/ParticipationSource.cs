namespace Humans.Domain.Enums;

/// <summary>
/// How an EventParticipation status was set.
/// </summary>
public enum ParticipationSource
{
    /// <summary>
    /// User self-declared (e.g. "Not attending this year").
    /// </summary>
    UserDeclared = 0,

    /// <summary>
    /// Automatically set by ticket sync.
    /// </summary>
    TicketSync = 1,

    /// <summary>
    /// Manually backfilled by an admin.
    /// </summary>
    AdminBackfill = 2,
}
