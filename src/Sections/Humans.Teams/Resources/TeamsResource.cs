namespace Humans.Teams;

/// <summary>
/// Marker type for Teams' resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Teams</c> —
/// <c>Humans.Teams.Resources</c> would make every teams string fall back to its raw key at
/// runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set is 190 keys across thirteen prefixes the section is the sole renderer of. Five keys
/// stay in <c>SharedResource</c> because something outside the section renders them too. Two
/// of those renderers were <c>Humans.UI</c> partials until G5 lane 4b-i
/// (nobodies-collective/Humans#866): <c>_RoleBadge</c> is now this section's own file but
/// Users' ProfileCard and Shell's gallery still render it, and <c>_HumanSearchResults</c> is
/// <c>Humans.Users</c>'. Neither can see this set, so the keys stay shared:
/// <c>Teams_Member</c> (<c>_RoleBadge</c>), <c>MyTeams_View</c> (<c>_HumanSearchResults</c>),
/// <c>Teams_Title</c> (four Shell pages), <c>MyTeams_Role</c> (<c>_RolesListContent</c>,
/// <c>UsersAdmin/AddRole</c>) and <c>TeamDetail_Actions</c> (<c>UsersAdmin/AdminDetail</c>).
/// Those call sites bind <c>SharedLocalizer</c>, as do the section's <c>Enum_SlotPriority_*</c>
/// and <c>Enum_RolePeriod_*</c> reads — both enums are Base's vocabulary (§15.3b, carve by
/// renderer per key).
/// </remarks>
public class TeamsResource;
