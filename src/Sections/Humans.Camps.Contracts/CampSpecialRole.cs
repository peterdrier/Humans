namespace Humans.Camps.Contracts;

/// <summary>
/// Non-<see cref="None"/> definitions are system-managed: <c>CampRoleService</c> rejects every edit except
/// <c>SlotCount</c> and <c>Description</c>.
/// </summary>
public enum CampSpecialRole
{
    None = 0,

    /// <summary>Camp management (edit, members, roles, leads) and camp-event submission.</summary>
    Lead = 1,

    /// <summary>Camp-event submission only; no camp-management authority.</summary>
    Workshop = 2,
}
