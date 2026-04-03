using System.Security.Claims;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Humans.Web.Authorization;

namespace Humans.Web.TagHelpers;

/// <summary>
/// Attribute-based TagHelper that conditionally renders an element based on
/// a named authorization policy. Suppresses the element when the current user
/// does not satisfy the policy.
///
/// Usage:
///   &lt;li authorize-policy="Admin"&gt;Only admins see this&lt;/li&gt;
///   &lt;div authorize-policy="CanAccessReviewQueue"&gt;...&lt;/div&gt;
/// </summary>
[HtmlTargetElement("*", Attributes = "authorize-policy")]
public class AuthorizeViewTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuthorizeViewTagHelper(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Named policy to evaluate. Must match a key in <see cref="ViewPolicies"/>.
    /// </summary>
    [HtmlAttributeName("authorize-policy")]
    public string Policy { get; set; } = "";

    /// <summary>
    /// Run before other TagHelpers to avoid unnecessary processing of suppressed elements.
    /// </summary>
    public override int Order => -1;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null || !ViewPolicies.Evaluate(Policy, user))
        {
            output.SuppressOutput();
            return;
        }

        // Remove the attribute from rendered HTML
        output.Attributes.RemoveAll("authorize-policy");
    }
}

/// <summary>
/// Centralized view-level authorization policies. Each policy maps a string name
/// to a role check function. Views reference policy names, not role combinations.
///
/// Add new policies here when new role-based visibility requirements emerge.
/// </summary>
public static class ViewPolicies
{
    public const string Admin = nameof(Admin);
    public const string Board = nameof(Board);
    public const string AdminOrBoard = nameof(AdminOrBoard);
    public const string TeamsAdminBoardOrAdmin = nameof(TeamsAdminBoardOrAdmin);
    public const string HumanAdminBoardOrAdmin = nameof(HumanAdminBoardOrAdmin);
    public const string HumanAdmin = nameof(HumanAdmin);
    public const string CampAdmin = nameof(CampAdmin);
    public const string CanAccessReviewQueue = nameof(CanAccessReviewQueue);
    public const string CanAccessTickets = nameof(CanAccessTickets);
    public const string CanManageTickets = nameof(CanManageTickets);
    public const string CanAccessVolunteers = nameof(CanAccessVolunteers);
    public const string CanAccessFinance = nameof(CanAccessFinance);
    public const string IsFeedbackAdmin = nameof(IsFeedbackAdmin);
    public const string CanManageDepartment = nameof(CanManageDepartment);
    public const string CanAccessDashboard = nameof(CanAccessDashboard);

    /// <summary>
    /// HumanAdmin who is NOT also Admin or Board. Used for the nav "Humans" link
    /// that only shows when the user has HumanAdmin but not the broader Board/Admin access
    /// (which already have their own Board nav link with fuller access).
    /// </summary>
    public const string HumanAdminOnly = nameof(HumanAdminOnly);

    /// <summary>
    /// Active member (Volunteers team member) OR TeamsAdmin/Board/Admin.
    /// Used for nav items that require active membership or administrative roles.
    /// </summary>
    public const string IsActiveMember = nameof(IsActiveMember);

    /// <summary>
    /// Active member (Volunteers team) OR has shift dashboard access.
    /// Used for Shifts nav visibility in main layout.
    /// </summary>
    public const string ActiveMemberOrShiftAccess = nameof(ActiveMemberOrShiftAccess);

    private static readonly Dictionary<string, Func<ClaimsPrincipal, bool>> Policies = new(StringComparer.Ordinal)
    {
        [Admin] = RoleChecks.IsAdmin,
        [Board] = RoleChecks.IsBoard,
        [AdminOrBoard] = RoleChecks.IsAdminOrBoard,
        [TeamsAdminBoardOrAdmin] = RoleChecks.IsTeamsAdminBoardOrAdmin,
        [HumanAdminBoardOrAdmin] = RoleChecks.IsHumanAdminBoardOrAdmin,
        [HumanAdmin] = RoleChecks.IsHumanAdmin,
        [CampAdmin] = RoleChecks.IsCampAdmin,
        [CanAccessReviewQueue] = RoleChecks.CanAccessReviewQueue,
        [CanAccessTickets] = RoleChecks.CanAccessTickets,
        [CanManageTickets] = RoleChecks.CanManageTickets,
        [CanAccessVolunteers] = RoleChecks.CanAccessVolunteers,
        [CanAccessFinance] = RoleChecks.CanAccessFinance,
        [IsFeedbackAdmin] = RoleChecks.IsFeedbackAdmin,
        [CanManageDepartment] = ShiftRoleChecks.CanManageDepartment,
        [CanAccessDashboard] = ShiftRoleChecks.CanAccessDashboard,
        [HumanAdminOnly] = user => RoleChecks.IsHumanAdmin(user) && !RoleChecks.IsAdminOrBoard(user),
        [IsActiveMember] = user =>
            user.HasClaim(RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
                RoleAssignmentClaimsTransformation.ActiveClaimValue) ||
            RoleChecks.IsTeamsAdminBoardOrAdmin(user),
        [ActiveMemberOrShiftAccess] = user =>
            user.HasClaim(RoleAssignmentClaimsTransformation.ActiveMemberClaimType,
                RoleAssignmentClaimsTransformation.ActiveClaimValue) ||
            RoleChecks.IsTeamsAdminBoardOrAdmin(user) ||
            ShiftRoleChecks.CanAccessDashboard(user),
    };

    /// <summary>
    /// Evaluates the named policy against the given user.
    /// Returns false for unknown policy names (fail-closed).
    /// </summary>
    public static bool Evaluate(string policyName, ClaimsPrincipal user)
    {
        return Policies.TryGetValue(policyName, out var check) && check(user);
    }
}
