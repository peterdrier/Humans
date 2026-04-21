namespace Humans.Web.Authorization;

/// <summary>
/// Canonical authorization policy names. Each name corresponds to a registered ASP.NET Core
/// authorization policy. Use these constants in [Authorize(Policy = ...)] attributes,
/// authorize-policy TagHelper attributes, and IAuthorizationService calls.
/// </summary>
public static class PolicyNames
{
    public const string AdminOnly = nameof(AdminOnly);
    public const string BoardOrAdmin = nameof(BoardOrAdmin);
    public const string HumanAdminBoardOrAdmin = nameof(HumanAdminBoardOrAdmin);
    public const string HumanAdminOrAdmin = nameof(HumanAdminOrAdmin);
    public const string TeamsAdminBoardOrAdmin = nameof(TeamsAdminBoardOrAdmin);
    public const string CampAdminOrAdmin = nameof(CampAdminOrAdmin);
    public const string TicketAdminBoardOrAdmin = nameof(TicketAdminBoardOrAdmin);
    public const string TicketAdminOrAdmin = nameof(TicketAdminOrAdmin);
    public const string FeedbackAdminOrAdmin = nameof(FeedbackAdminOrAdmin);
    public const string FinanceAdminOrAdmin = nameof(FinanceAdminOrAdmin);
    public const string ReviewQueueAccess = nameof(ReviewQueueAccess);
    public const string ConsentCoordinatorBoardOrAdmin = nameof(ConsentCoordinatorBoardOrAdmin);
    public const string ShiftDashboardAccess = nameof(ShiftDashboardAccess);
    public const string ShiftDepartmentManager = nameof(ShiftDepartmentManager);
    public const string PrivilegedSignupApprover = nameof(PrivilegedSignupApprover);
    public const string VolunteerManager = nameof(VolunteerManager);
    public const string VolunteerSectionAccess = nameof(VolunteerSectionAccess);
    public const string MedicalDataViewer = nameof(MedicalDataViewer);

    /// <summary>
    /// Active member (Volunteers team) OR has shift dashboard access roles.
    /// Composite policy requiring a custom authorization handler.
    /// </summary>
    public const string ActiveMemberOrShiftAccess = nameof(ActiveMemberOrShiftAccess);

    /// <summary>
    /// Active member (Volunteers team member) OR TeamsAdmin/Board/Admin.
    /// Composite policy requiring a custom authorization handler.
    /// </summary>
    public const string IsActiveMember = nameof(IsActiveMember);

    /// <summary>
    /// HumanAdmin who is NOT also Admin or Board. Used for the nav "Humans" link
    /// that only shows when the user has HumanAdmin but not the broader Board/Admin access.
    /// Composite policy requiring a custom authorization handler.
    /// </summary>
    public const string HumanAdminOnly = nameof(HumanAdminOnly);

    /// <summary>
    /// Board member (standalone). Used rarely — most Board gates also include Admin.
    /// </summary>
    public const string BoardOnly = nameof(BoardOnly);
}
