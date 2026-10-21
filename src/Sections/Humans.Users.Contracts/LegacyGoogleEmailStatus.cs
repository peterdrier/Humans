namespace Humans.Users.Contracts;

/// <summary>
/// The frozen per-address Google sync verdict on <c>user_emails.GoogleEmailStatus</c> (and the
/// deprecated <c>users.GoogleEmailStatus</c>). The live status is GoogleIntegration's
/// <c>GoogleEmailStatus</c> on <c>google_workspace_identities</c> (Users decomposition commit 15);
/// this type only carries the legacy value to the <c>google.workspace-identities</c> copy and
/// read fallback, and dies with them.
/// </summary>
public enum LegacyGoogleEmailStatus
{
    /// <summary>Not validated against Google APIs.</summary>
    Unknown = 0,

    /// <summary>Successfully used with Google APIs.</summary>
    Valid = 1,

    /// <summary>Permanently rejected by Google APIs.</summary>
    Rejected = 2
}
