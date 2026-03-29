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

    /// <summary>
    /// Teams Administrator — can manage all teams, approve membership, assign leads,
    /// and configure Google Group prefixes system-wide.
    /// </summary>
    public const string TeamsAdmin = "TeamsAdmin";

    /// <summary>
    /// Camp Administrator — can manage camps, approve/reject season registrations,
    /// and configure camp settings system-wide.
    /// </summary>
    public const string CampAdmin = "CampAdmin";

    /// <summary>
    /// Ticket Administrator — can manage ticket vendor integration, trigger syncs,
    /// generate discount codes, and export ticket data.
    /// </summary>
    public const string TicketAdmin = "TicketAdmin";

    /// <summary>
    /// NoInfo Administrator — can approve/voluntell shift signups but NOT create/edit shifts.
    /// Has access to volunteer event profile medical data.
    /// </summary>
    public const string NoInfoAdmin = "NoInfoAdmin";

    /// <summary>
    /// Feedback Administrator — can view all feedback reports, respond to reporters,
    /// manage feedback status, and link GitHub issues.
    /// </summary>
    public const string FeedbackAdmin = "FeedbackAdmin";

    /// <summary>
    /// Human Administrator — can view human admin pages, approve/suspend/reject humans,
    /// provision @nobodies.team email accounts, and manage role assignments.
    /// </summary>
    public const string HumanAdmin = "HumanAdmin";

    /// <summary>
    /// Finance Administrator — can manage budgets, budget years, groups, categories,
    /// and line items. Full access to the Finance section.
    /// </summary>
    public const string FinanceAdmin = "FinanceAdmin";
}
