namespace Humans.Teams;

/// <summary>
/// The .resx files sit beside this file, so it must stay in <c>namespace Humans.Teams</c>: the manifest name comes
/// from this file's namespace, and any other makes every string fall back to its key. Public because the boot
/// localization check finds markers through <c>GetExportedTypes()</c>.
/// </summary>
/// <remarks>Keys other sections render too (e.g. <c>Teams_Member</c>, <c>MyTeams_Role</c>) stay in SharedResource.</remarks>
public class TeamsResource;
