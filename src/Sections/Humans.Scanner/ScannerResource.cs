namespace Humans.Scanner;

/// <summary>
/// Marker type for Scanner's resource set. The <c>.resx</c> files sit beside this file: the
/// SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's namespace,
/// not the folder path, so this must stay <c>namespace Humans.Scanner</c> or every Scanner
/// string falls back to its raw key at runtime.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence.
/// </remarks>
public class ScannerResource;
