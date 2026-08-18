namespace Humans.Calendar;

/// <summary>
/// Marker type for Calendar's resource set. The <c>.resx</c> files sit beside this file
/// on purpose: the SDK derives the manifest name from the adjacent same-named
/// <c>.cs</c> file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Calendar</c> — <c>Humans.Calendar.Resources</c> would make every
/// Calendar string fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers
/// via <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// </remarks>
public class CalendarResource;
