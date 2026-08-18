namespace Humans.Scanner;

/// <summary>
/// Marker type for Scanner's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Scanner</c> —
/// <c>Humans.Scanner.Resources</c> would make every Scanner string fall back to its raw key
/// at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence
/// (G5-SECTION-TEMPLATE.md step 3b).
/// </remarks>
public class ScannerResource;
