namespace Humans.Rideshare.Contracts;

/// <summary>Rideshare's grantable role. Declared to Auth by Rideshare's <c>Section</c>.</summary>
public static class RideshareRoles
{
    /// <summary>
    /// Rideshare Admin: sets the year's destination and travel windows, views season statistics and the
    /// operational day roster. Confers nothing outside Rideshare.
    /// </summary>
    public const string RideshareAdmin = "RideshareAdmin";
}
