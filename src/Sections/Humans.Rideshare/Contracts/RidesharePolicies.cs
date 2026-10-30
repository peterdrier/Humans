namespace Humans.Rideshare.Contracts;

/// <summary>Rideshare's authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class RidesharePolicies
{
    /// <summary>Rideshare Admin, Board or Admin: the rideshare admin pages.</summary>
    public const string Read = "RideshareRead";

    /// <summary>Rideshare Admin or Admin: the year's destination and travel windows.</summary>
    public const string Manage = "RideshareManage";
}
