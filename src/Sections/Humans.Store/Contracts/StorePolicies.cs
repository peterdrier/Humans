namespace Humans.Store.Contracts;

/// <summary>Store's authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class StorePolicies
{
    /// <summary>Store Admin, Finance Admin, Board or Admin: the store admin pages.</summary>
    public const string Read = "StoreRead";

    /// <summary>Store Admin, Finance Admin or Admin: catalog changes and payment repairs.</summary>
    public const string Manage = "StoreManage";
}
