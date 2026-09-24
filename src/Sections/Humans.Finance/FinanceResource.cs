namespace Humans.Finance;

/// <summary>
/// Marker type for Finance's resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named
/// <c>.cs</c> file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Finance</c> — <c>Humans.Finance.Resources</c> would make every
/// Finance string fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers
/// via <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// Finance's screens are admin-only and carry no keys; the set exists for the mail it
/// sends members (peterdrier/Humans#1820).
/// </remarks>
public class FinanceResource;
