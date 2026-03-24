namespace Humans.Domain.Enums;

/// <summary>
/// Categories of system communications for preference management.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum MessageCategory
{
    /// <summary>
    /// Critical system messages (account, consent, security). Always on — cannot opt out.
    /// </summary>
    System = 0,

    /// <summary>
    /// Shift changes, schedule updates, team additions. Default: on.
    /// </summary>
    EventOperations = 1,

    /// <summary>
    /// General community news and facilitated messages. Default: off.
    /// </summary>
    CommunityUpdates = 2,

    /// <summary>
    /// Campaign emails, promotions. Default: off.
    /// </summary>
    Marketing = 3
}
