namespace Humans.Auth;

/// <summary>
/// Marker type for Auth's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Auth</c>.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set holds the magic-link email copy Auth owns (<c>Auth_Email_*</c>) and the copy of the
/// role-assignment admin pages and their admin-detail card (<c>Auth_Roles_*</c>, <c>Auth_AddRole_*</c>).
/// The sign-in *pages* live in Shell, so their <c>Login_*</c>/<c>MagicLink*</c>/<c>GateLogin_*</c>
/// keys stay in <c>SharedResource</c> with them, as do the shared <c>Common_</c>, <c>MyTeams_Role</c>,
/// <c>AdminHuman_Active</c> and <c>Admin_Role*</c> flash-message keys the role pages read.
/// </remarks>
public class AuthResource;
