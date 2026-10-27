using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Finance.Contracts;

namespace Humans.Store.Contracts;

/// <summary>Store's grantable role and its predicate. Declared to Auth by Store's <c>Section</c>.</summary>
public static class StoreRoles
{
    /// <summary>
    /// Store Admin: the Store domain end to end (catalog, prices, VAT, deposits, deadlines, orders, payments,
    /// invoices, treasury sync). Finance Admin keeps parallel access for accounting work.
    /// </summary>
    public const string StoreAdmin = "StoreAdmin";

    /// <summary>Admin, Store Admin or Finance Admin (memory/code/admin-role-superset.md).</summary>
    public static bool CanAdministerStore(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user)
        || user.IsInRole(StoreAdmin)
        || user.IsInRole(FinanceRoles.FinanceAdmin);
}
