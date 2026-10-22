namespace Humans.Base.Constants;

public static class RoleNames
{
    public const string Admin = "Admin";

    public const string Board = "Board";

    /// <summary>Consent Coordinator: performs safety checks on new humans during onboarding (clear or flag).</summary>
    public const string ConsentCoordinator = "ConsentCoordinator";

    /// <summary>Volunteer Coordinator: facilitation contact for onboarding humans; read-only on the review queue.</summary>
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
    /// Guide Moderator — can approve, reject, and request edits on event guide submissions.
    /// </summary>
    public const string EventsAdmin = "EventsAdmin";

    /// <summary>
    /// Feedback Administrator: grants <b>no</b> feedback access, since the retired Feedback section is gated by
    /// <c>AdminOnly</c>. Stays assignable because the Staff page, the Guide role resolver, the pill-filter labels and
    /// <c>AnyAdminRole</c> still reference it.
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

    /// <summary>
    /// Store Administrator — Store-domain superset: catalog (products, prices, VAT, deposits,
    /// deadlines), orders, payments, invoices, and treasury sync. FinanceAdmin retains parallel
    /// access for accounting workflows.
    /// </summary>
    public const string StoreAdmin = "StoreAdmin";

    /// <summary>
    /// Cantina Administrator — can view the cantina weekly/daily roster (on-site cohort,
    /// dietary breakdown, allergy/intolerance rollups) to plan meals. Read-only; sees
    /// dietary preferences but never medical conditions (excluded at the DTO boundary).
    /// </summary>
    public const string CantinaAdmin = "CantinaAdmin";

    /// <summary>
    /// Early-Entry Team Administrator — a cross-team role that can grant, edit, and
    /// revoke early-entry grants on ANY team that has EarlyEntryEnabled. Coordinators
    /// manage early entry for their own team without this role; this role exists for
    /// mid-level admins who manage EE across teams. Confers nothing beyond EE
    /// management; granted individually via the existing role-assignment flow.
    /// </summary>
    public const string EETeamAdmin = "EETeamAdmin";

    /// <summary>
    /// Rideshare Administrator — sets the year's rideshare destination and travel windows,
    /// views season statistics, and views the operational day roster (every trip on a date
    /// with its accepted riders). Confers nothing outside the Rideshare section.
    /// </summary>
    public const string RideshareAdmin = "RideshareAdmin";

    /// <summary>
    /// Every role name defined above, in display order. The single source of truth
    /// for UI that enumerates roles (e.g. the role-assignment filter bar) so new
    /// roles surface automatically. Completeness is enforced by
    /// <c>RoleNamesTests.All_ContainsEveryDefinedRoleConstant</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Admin,
        Board,
        HumanAdmin,
        ConsentCoordinator,
        VolunteerCoordinator,
        TeamsAdmin,
        CampAdmin,
        TicketAdmin,
        NoInfoAdmin,
        EventsAdmin,
        FeedbackAdmin,
        FinanceAdmin,
        StoreAdmin,
        CantinaAdmin,
        EETeamAdmin,
        RideshareAdmin
    ];

    /// <summary>Roles Board and HumanAdmin may assign and end.</summary>
    public static readonly IReadOnlySet<string> BoardManageableRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        Board,
        HumanAdmin,
        TeamsAdmin,
        CampAdmin,
        TicketAdmin,
        NoInfoAdmin,
        FeedbackAdmin,
        FinanceAdmin,
        EventsAdmin,
        StoreAdmin,
        CantinaAdmin,
        EETeamAdmin,
        RideshareAdmin,
        ConsentCoordinator,
        VolunteerCoordinator
    };
}
