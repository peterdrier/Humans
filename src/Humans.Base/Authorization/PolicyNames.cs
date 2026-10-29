namespace Humans.Base.Authorization;

/// <summary>
/// The authorization policies no one section owns: the two system-wide roles, app access, the admin shell, and the
/// composites that span sections. A section's own policies live on its <c>&lt;Section&gt;Policies</c> class in its
/// Contracts (<c>TeamsPolicies.Read</c>, <c>TeamsPolicies.Manage</c>).
/// </summary>
public static class PolicyNames
{
    public const string AdminOnly = nameof(AdminOnly);
    public const string BoardOrAdmin = nameof(BoardOrAdmin);

    public const string ReviewQueueAccess = nameof(ReviewQueueAccess);
    public const string ConsentCoordinatorBoardOrAdmin = nameof(ConsentCoordinatorBoardOrAdmin);

    /// <summary>
    /// Scanner tools (/Scanner/*): TicketsAdmin, Board, or Admin — plus the shared
    /// gate-terminal account (matched by its well-known id, not a role, so it never
    /// shows up in role-assignment UI). Composite assertion policy.
    /// </summary>
    public const string ScannerAccess = nameof(ScannerAccess);

    /// <summary>
    /// Gate <em>write</em> actions (/Gate/Decision, /Gate/Claim POST): records a
    /// durable admission or claims the scanning session. TicketsAdmin, Admin, or the
    /// shared gate-terminal account; Board reads the gate through
    /// <see cref="ScannerAccess"/> and admits no one. Composite assertion policy.
    /// </summary>
    public const string GateAdmit = nameof(GateAdmit);

    /// <summary>Can use the app: <c>UserState == Active</c>. The single nav-visibility gate.</summary>
    public const string AppAccess = nameof(AppAccess);

    /// <summary>
    /// Board member (standalone). Used rarely — most Board gates also include Admin.
    /// </summary>
    public const string BoardOnly = nameof(BoardOnly);

    /// <summary>
    /// City-planning map admin: CampsAdmin OR Admin OR a member of the city-planning team.
    /// The same audience <c>CityPlanningController</c> self-gates on, so the
    /// <c>/Settings#city-planning</c> tab is visible to exactly the people its POST
    /// endpoints accept. Composite policy requiring a custom authorization handler.
    /// </summary>
    public const string CityPlanningMapAdmin = nameof(CityPlanningMapAdmin);

    /// <summary>
    /// Every catalogued role that opens the admin shell (<c>RoleDefinition.OpensAdminShell</c>), derived from the
    /// sections' <c>ISectionRoles</c> through Auth's <c>RoleCatalog</c>. Gates the admin-shell entry
    /// point (/Admin) so domain admins (e.g. FinanceAdmin) can reach the dashboard
    /// and see only the sidebar items their per-item authorization permits. The
    /// dashboard tiles themselves are aggregate counts that are safe across all
    /// admin roles.
    /// </summary>
    public const string AnyAdminRole = nameof(AnyAdminRole);

    /// <summary>
    /// May assign or end the role passed as the authorization <em>resource</em> (a role-name
    /// string): <c>AuthorizeAsync(User, roleName, PolicyNames.RoleAssignmentManage)</c>.
    /// Backed by Auth's resource-based <c>RoleAssignmentAuthorizationHandler</c>. The name
    /// exists so callers outside <c>Humans.Auth</c> — <c>UsersAdminController</c> — can reach
    /// the gate without naming the requirement type, which lives inside the section.
    /// </summary>
    public const string RoleAssignmentManage = nameof(RoleAssignmentManage);

    /// <summary>
    /// May view an expense report (the resource): submitter, its budget category's coordinator, or FinanceAdmin/Admin.
    /// Registered by Expenses' <c>Section</c> so an external caller (Backdoor's finance read API) reaches the
    /// same check as <c>ExpensesController</c> without the requirement type leaving the section.
    /// </summary>
    public const string ExpenseReportView = nameof(ExpenseReportView);
}
