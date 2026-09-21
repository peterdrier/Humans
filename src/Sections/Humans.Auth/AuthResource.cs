namespace Humans.Auth;

/// <summary>
/// Marker type for Auth's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Auth</c>
/// (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set holds only the magic-link email copy Auth owns (peterdrier/Humans#1651). The
/// sign-in *pages* still live in Shell, so their <c>Login_*</c>/<c>MagicLink*</c>/
/// <c>GateLogin_*</c> keys stay in <c>SharedResource</c> with them.
/// </remarks>
public class AuthResource;
