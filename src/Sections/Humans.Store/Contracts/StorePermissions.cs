namespace Humans.Store.Contracts;

/// <summary>
/// The permission key Store checks, declared by its <c>Section</c>. Only holdings on a department count.
/// </summary>
public static class StorePermissions
{
    /// <summary>
    /// Create and edit the department's team order. Store Admin and Teams Admin hold it everywhere.
    /// </summary>
    public const string TeamOrders = "Store.TeamOrders";
}
