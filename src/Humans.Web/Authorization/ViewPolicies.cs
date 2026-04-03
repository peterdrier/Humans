using System.Security.Claims;

namespace Humans.Web.Authorization;

/// <summary>
/// Legacy view-level authorization policy evaluator.
/// <para>
/// <b>Deprecated:</b> Use <c>authorize-policy</c> TagHelper attributes (which delegate to
/// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>) or inject
/// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/> directly instead.
/// This class will be removed once all direct Razor calls are migrated to the TagHelper
/// or IAuthorizationService.
/// </para>
/// </summary>
// TODO: Remove after Phase 1 section migration — use authorize-policy TagHelper or IAuthorizationService instead.
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
    public const string HumanAdminOnly = nameof(HumanAdminOnly);
    public const string IsActiveMember = nameof(IsActiveMember);
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
