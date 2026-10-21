namespace Humans.Grants;

/// <summary>
/// Marker type for Grants' resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c>
/// file's namespace, so this must stay <c>namespace Humans.Grants</c>.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence.
/// </remarks>
public class GrantsResource;
