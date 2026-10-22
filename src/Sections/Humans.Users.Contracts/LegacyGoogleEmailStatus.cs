namespace Humans.Users.Contracts;

/// <summary>
/// The frozen <c>user_emails.GoogleEmailStatus</c> type; it only carries legacy values to the
/// <c>google.workspace-identities</c> copy and read fallback, and goes with them.
/// </summary>
public enum LegacyGoogleEmailStatus
{
    Unknown = 0,

    Valid = 1,

    Rejected = 2
}
