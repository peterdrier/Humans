namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Google's verdict on a member's Workspace identity address, from sync. Lives on
/// <c>google_workspace_identities</c> with the address it was reached for: a different address
/// starts <see cref="Unknown"/>, so a rejection never outlives the address Google refused.
/// </summary>
public enum GoogleEmailStatus
{
    Unknown = 0,

    Valid = 1,

    /// <summary>
    /// Permanently rejected by Google APIs (HTTP 400/403/404, "no Google account"). Sync skips
    /// the member until a different address becomes the identity or the address is re-asserted.
    /// </summary>
    Rejected = 2
}
