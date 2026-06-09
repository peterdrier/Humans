namespace Humans.Web.Authorization;

/// <summary>
/// Canonical authorization policy names. Each name corresponds to a registered ASP.NET Core
/// authorization policy. Use these constants in [Authorize(Policy = ...)] attributes,
/// authorize-policy TagHelper attributes, and IAuthorizationService calls.
/// </summary>
public static class PolicyNames
{
    public const string AgentRateLimit = nameof(AgentRateLimit);
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
    public const string EventsAdminOrAdmin = nameof(EventsAdminOrAdmin);
    public const string CantinaAdminOrAdmin = nameof(CantinaAdminOrAdmin);
    public const string StoreCatalogAdmin = nameof(StoreCatalogAdmin);
    public const string ReviewQueueAccess = nameof(ReviewQueueAccess);
    public const string ConsentCoordinatorBoardOrAdmin = nameof(ConsentCoordinatorBoardOrAdmin);
    public const string ShiftDashboardAccess = nameof(ShiftDashboardAccess);
    public const string VolunteerTrackingWrite = nameof(VolunteerTrackingWrite);
    public const string ShiftDepartmentManager = nameof(ShiftDepartmentManager);
    public const string PrivilegedSignupApprover = nameof(PrivilegedSignupApprover);
    public const string VolunteerManager = nameof(VolunteerManager);
    public const string MedicalDataViewer = nameof(MedicalDataViewer);

    /// <summary>
    /// Can use the app: <c>UserState == Active</c> (entered legal name).
    /// The single nav-visibility gate replaces the former
    /// IsActiveMember / ActiveMemberOrShiftAccess split (there is no separate shift access).
    /// </summary>
    public const string AppAccess = nameof(AppAccess);

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

    /// <summary>
    /// Read-only Barrios compliance matrix: CampAdmin OR Admin OR a coordinator /
    /// management role-holder on any team or sub-team. Broader than
    /// <see cref="CampAdminOrAdmin"/> (which gates the camp-management surface) so
    /// team coordinators can also see role staffing across barrios. Composite
    /// policy requiring a custom authorization handler.
    /// </summary>
    public const string CampComplianceAccess = nameof(CampComplianceAccess);

    /// <summary>
    /// Any admin-shaped role: Admin, Board, HumanAdmin, TeamsAdmin, CampAdmin,
    /// TicketAdmin, EventsAdmin, FeedbackAdmin, FinanceAdmin, NoInfoAdmin,
    /// VolunteerCoordinator, or ConsentCoordinator. Gates the admin-shell entry
    /// point (/Admin) so domain admins (e.g. FinanceAdmin) can reach the dashboard
    /// and see only the sidebar items their per-item authorization permits. The
    /// dashboard tiles themselves are aggregate counts that are safe across all
    /// admin roles.
    /// </summary>
    public const string AnyAdminRole = nameof(AnyAdminRole);
}
