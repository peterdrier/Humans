namespace Humans.Domain.Constants;

/// <summary>
/// Constants for role names used in the application.
/// </summary>
public static class RoleNames
{
    /// <summary>
    /// Administrator role with full system access.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Board member role with elevated permissions.
    /// </summary>
    public const string Board = "Board";

    /// <summary>
    /// Consent Coordinator — performs safety checks on new humans during onboarding.
    /// Can clear or flag consent checks. Bypasses MembershipRequiredFilter.
    /// </summary>
    public const string ConsentCoordinator = "ConsentCoordinator";

    /// <summary>
    /// Volunteer Coordinator — facilitation contact for onboarding humans.
    /// Read-only access to onboarding review queue. Bypasses MembershipRequiredFilter.
    /// </summary>
    public const string VolunteerCoordinator = "VolunteerCoordinator";
}
