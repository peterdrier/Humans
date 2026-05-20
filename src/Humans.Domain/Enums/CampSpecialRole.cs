namespace Humans.Domain.Enums;

/// <summary>
/// Marker for a special, system-managed <c>CampRoleDefinition</c> with extra
/// authorization semantics. Most definitions are <see cref="None"/>: regular
/// CampAdmin-managed entries with no special authority. Non-<see cref="None"/>
/// rows are immutable (rename, slug, sort, min-required, deactivation rejected
/// by <c>CampRoleService</c>) — only <c>SlotCount</c> and <c>Description</c>
/// can be edited. Seeded by the CampAdmin "Seed system roles" admin button
/// (idempotent across every non-None value).
/// </summary>
public enum CampSpecialRole
{
    /// <summary>Regular admin-managed role. No special authority.</summary>
    None = 0,

    /// <summary>
    /// Authorizes camp-management actions (Edit, members, roles, leads) and
    /// camp-event submission via <c>EventsController</c>.
    /// </summary>
    Lead = 1,

    /// <summary>
    /// Authorizes camp-event submission via <c>EventsController</c>
    /// alongside <see cref="Lead"/>. Does NOT confer general camp-management
    /// authority.
    /// </summary>
    Workshop = 2,
}
