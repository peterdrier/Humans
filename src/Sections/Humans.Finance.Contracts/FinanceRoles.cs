using System.Security.Claims;
using Humans.Base.Authorization;

namespace Humans.Finance.Contracts;

/// <summary>Finance's grantable role and its predicate. Declared to Auth by Finance's <c>Section</c>.</summary>
public static class FinanceRoles
{
    /// <summary>Finance Admin: budgets, budget years, groups, categories and line items; the whole Finance section.</summary>
    public const string FinanceAdmin = "FinanceAdmin";

    /// <summary>Admin or Finance Admin.</summary>
    public static bool IsFinanceAdmin(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user) || user.IsInRole(FinanceAdmin);
}
