namespace Humans.Users.Contracts;

/// <summary>
/// The frozen per-address Google sync verdict on <c>user_emails.GoogleEmailStatus</c> (and the
/// deprecated <c>users.GoogleEmailStatus</c>). The live status is GoogleIntegration's
/// <c>GoogleEmailStatus</c> on <c>google_workspace_identities</c> (Users decomposition commit 15);
/// this type types the frozen columns, which only Users' GDPR export still reads, and dies with
/// their drop (USERS-21). It stays public because the public <c>UserEmail</c> and <c>User</c>
/// entities carry it.
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
