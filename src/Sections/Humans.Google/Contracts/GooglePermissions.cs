namespace Humans.Google.Contracts;

/// <summary>
/// The permission key Google checks, declared by its <c>Section</c>. Only holdings on a department count.
/// </summary>
public static class GooglePermissions
{
    /// <summary>
    /// Link and unlink the department's Drive folders and Google Groups, and change their settings. Checked only
    /// while coordinators may manage resources at all; Teams Admin holds it everywhere.
    /// </summary>
    public const string ManageResources = "Google.ManageResources";
}
