namespace Humans.Domain.Constants;

/// <summary>
/// Shared role combinations used in authorization attributes.
/// </summary>
public static class RoleGroups
{
    public const string BoardOrAdmin = RoleNames.Board + "," + RoleNames.Admin;

    public const string CampAdminOrAdmin = RoleNames.CampAdmin + "," + RoleNames.Admin;

    public const string ConsentCoordinatorBoardOrAdmin =
        RoleNames.ConsentCoordinator + "," + RoleNames.Board + "," + RoleNames.Admin;

    public const string ReviewQueueAccess =
        RoleNames.ConsentCoordinator + "," + RoleNames.VolunteerCoordinator + "," + RoleNames.Board + "," + RoleNames.Admin;

    public const string TeamsAdminBoardOrAdmin =
        RoleNames.TeamsAdmin + "," + RoleNames.Board + "," + RoleNames.Admin;

    public const string TicketAdminBoardOrAdmin =
        RoleNames.TicketAdmin + "," + RoleNames.Admin + "," + RoleNames.Board;

    public const string TicketAdminOrAdmin = RoleNames.TicketAdmin + "," + RoleNames.Admin;

    public const string FeedbackAdminOrAdmin = RoleNames.FeedbackAdmin + "," + RoleNames.Admin;

    public const string HumanAdminBoardOrAdmin =
        RoleNames.HumanAdmin + "," + RoleNames.Board + "," + RoleNames.Admin;

    public const string HumanAdminOrAdmin = RoleNames.HumanAdmin + "," + RoleNames.Admin;

    public const string FinanceAdminOrAdmin = RoleNames.FinanceAdmin + "," + RoleNames.Admin;
}
