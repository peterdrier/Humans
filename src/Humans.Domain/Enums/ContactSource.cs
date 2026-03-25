namespace Humans.Domain.Enums;

/// <summary>
/// Where an external contact was imported from.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum ContactSource
{
    /// <summary>
    /// Manually created by an admin.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Imported from MailerLite mailing list.
    /// </summary>
    MailerLite = 1,

    /// <summary>
    /// Imported from TicketTailor ticket purchase.
    /// </summary>
    TicketTailor = 2
}
