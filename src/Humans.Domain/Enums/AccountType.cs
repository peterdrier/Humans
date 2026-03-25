namespace Humans.Domain.Enums;

/// <summary>
/// Distinguishes full members from lightweight external contacts.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum AccountType
{
    /// <summary>
    /// Full platform member (signed up via OAuth, has profile, teams, etc.).
    /// </summary>
    Member = 0,

    /// <summary>
    /// External contact imported from MailerLite, TicketTailor, or manual entry.
    /// No login, no profile — exists for communication preference tracking.
    /// </summary>
    Contact = 1,

    /// <summary>
    /// Deactivated account (e.g., after contact-to-member merge).
    /// Preserved for audit trail.
    /// </summary>
    Deactivated = 2
}
